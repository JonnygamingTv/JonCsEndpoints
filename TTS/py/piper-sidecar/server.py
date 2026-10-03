#!/usr/bin/env python3
"""
Piper TTS sidecar — FastAPI/uvicorn over a Unix domain socket.
Models are loaded once at startup; synthesis is CPU-bound work
dispatched to asyncio's thread pool so the event loop stays free.

Endpoints:
  POST /v1/audio/speech   — OpenAI-compatible
  GET  /v1/models         — list loaded voices
  GET  /health
"""

import asyncio
import glob
import io, wave
import json
import os
import subprocess
import tempfile
import threading
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException, Response
from fastapi.middleware.cors import CORSMiddleware
from piper import PiperVoice, SynthesisConfig
from pydantic import BaseModel

# ── Config ─────────────────────────────────────────────────────────────────
VOICES_DIR   = Path(os.getenv("VOICES_DIR",  "/opt/piper-voices"))
SOCKET_PATH  = Path(os.getenv("SOCKET_PATH", "/run/piper-tts/piper.sock"))
# How many synthesis threads to allow concurrently.
# Piper ONNX is already multi-threaded internally; too many threads here
# just contend on memory bandwidth. On a 28-core Xeon, 6–8 is a good start.
MAX_SYNTH_THREADS = int(os.getenv("MAX_SYNTH_THREADS", "6"))

# ── App & thread pool ───────────────────────────────────────────────────────
app  = FastAPI(title="Piper TTS Sidecar", docs_url=None, redoc_url=None)
pool = ThreadPoolExecutor(max_workers=MAX_SYNTH_THREADS, thread_name_prefix="piper")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["POST", "GET", "OPTIONS"],
    allow_headers=["*"],
)

# ── Voice registry ──────────────────────────────────────────────────────────
# voice_id → {"voice": PiperVoice, "config": dict, "lang": str, "quality": str}
_voices: dict = {}
_voices_lock  = threading.Lock()


def _load_voices():
    """Scan VOICES_DIR and load all .onnx models into memory."""
    loaded = {}
    for onnx_path in VOICES_DIR.glob("**/*.onnx"):
        voice_id = onnx_path.stem          # e.g. en_US-ryan-high
        config_path = onnx_path.with_suffix(".onnx.json")
        if not config_path.exists():
            print(f"[piper] Skipping {voice_id}: no .json config found")
            continue
        try:
            voice = PiperVoice.load(str(onnx_path), config_path=str(config_path), use_cuda=False)
            parts = voice_id.split("-")
            loaded[voice_id] = {
                "voice":   voice,
                "lang":    parts[0] if len(parts) > 0 else "unknown",
                "name":    parts[1] if len(parts) > 1 else voice_id,
                "quality": parts[2] if len(parts) > 2 else "medium",
            }
            print(f"[piper] Loaded: {voice_id}")
        except Exception as e:
            print(f"[piper] Failed to load {voice_id}: {e}")

    with _voices_lock:
        _voices.clear()
        _voices.update(loaded)

    print(f"[piper] {len(loaded)} voice(s) ready.")


@app.on_event("startup")
def startup():
    _load_voices()
    # Watch for new voices dropped in at runtime (optional, lightweight)
    _start_voice_watcher()


# ── Voice watcher (inotifywait-free, polling every 30s) ────────────────────
def _start_voice_watcher():
    def _watch():
        import time
        last = set(VOICES_DIR.glob("**/*.onnx"))
        while True:
            time.sleep(30)
            current = set(VOICES_DIR.glob("**/*.onnx"))
            if current != last:
                print("[piper] Voice directory changed — reloading...")
                _load_voices()
                last = current
    t = threading.Thread(target=_watch, daemon=True)
    t.start()


# ── Synthesis ───────────────────────────────────────────────────────────────
def _synthesise_wav(voice: PiperVoice, text: str, length_scale: float) -> bytes:
    """Run in thread pool — returns raw WAV bytes. Piper 1.x API."""
    buf = io.BytesIO()
    syn_config = SynthesisConfig(length_scale=length_scale)
    # synthesize_wav writes a complete, well-formed WAV to the wave.Wave_write handle.
    # wave.open on a BytesIO gives us that handle directly.
    with wave.open(buf, "wb") as wav_file:
        voice.synthesize_wav(text, wav_file, syn_config=syn_config)
    return buf.getvalue()

def _wav_to_mp3(wav_bytes: bytes) -> bytes:
    """Convert WAV bytes → MP3 bytes via ffmpeg (runs in thread pool)."""
    result = subprocess.run(
        ["ffmpeg", "-y", "-f", "wav", "-i", "pipe:0",
         "-q:a", "2", "-f", "mp3", "pipe:1"],
        input=wav_bytes,
        capture_output=True,
        timeout=30,
    )
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg error: {result.stderr.decode()[:200]}")
    return result.stdout


# ── Request model ────────────────────────────────────────────────────────────
class SpeechRequest(BaseModel):
    input: str
    model: Optional[str] = None          # voice id; falls back to first loaded
    response_format: Optional[str] = "mp3"
    speed: Optional[float] = 1.0         # OpenAI speed param (inverted → length_scale)


# ── Routes ───────────────────────────────────────────────────────────────────
@app.post("/v1/audio/speech")
async def speech(req: SpeechRequest):
    if not req.input or not req.input.strip():
        raise HTTPException(400, "Missing 'input'")
    if len(req.input) > 4000:
        raise HTTPException(400, "Input too long (max 4000 chars)")

    fmt = (req.response_format or "mp3").lower()
    if fmt not in ("mp3", "wav"):
        fmt = "mp3"

    with _voices_lock:
        voice_id = req.model if req.model and req.model in _voices else (
            next(iter(_voices), None)
        )
        entry = _voices.get(voice_id) if voice_id else None

    if not entry:
        raise HTTPException(503, "No voices available")

    # OpenAI speed > 1 = faster → Piper length_scale < 1 (inverted)
    speed = max(0.25, min(4.0, req.speed or 1.0))
    length_scale = round(1.0 / speed, 3)

    loop = asyncio.get_event_loop()

    # WAV synthesis — CPU-bound, runs in thread pool
    wav_bytes = await loop.run_in_executor(
        pool, _synthesise_wav, entry["voice"], req.input.strip(), length_scale
    )

    if fmt == "wav":
        return Response(content=wav_bytes, media_type="audio/wav")

    # MP3 conversion — also CPU-bound (ffmpeg subprocess)
    mp3_bytes = await loop.run_in_executor(pool, _wav_to_mp3, wav_bytes)
    return Response(content=mp3_bytes, media_type="audio/mpeg")


@app.get("/v1/models")
@app.get("/v1/audio/voices")
def list_voices():
    with _voices_lock:
        data = [
            {
                "id":       vid,
                "object":   "model",
                "owned_by": "piper",
                "name":     v["name"],
                "language": v["lang"],
                "quality":  v["quality"],
            }
            for vid, v in _voices.items()
        ]
    return {"object": "list", "data": data}


@app.get("/health")
def health():
    with _voices_lock:
        count = len(_voices)
    return {"status": "ok", "voices_loaded": count}


# ── Entrypoint ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    import uvicorn
    SOCKET_PATH.parent.mkdir(parents=True, exist_ok=True)
    # Remove stale socket from a previous run
    SOCKET_PATH.unlink(missing_ok=True)
    uvicorn.run(
        "server:app",
        uds=str(SOCKET_PATH),
        workers=1,           # single worker: all models share one process
        log_level="warning",
        access_log=False,
    )
