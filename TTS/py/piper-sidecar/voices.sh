cd /opt/piper-voices
HF="https://huggingface.co/rhasspy/piper-voices/resolve/main"

# ljspeech high — warm audiobook narrator female, the best stock female voice
wget -q --show-progress "$HF/en/en_US/ljspeech/high/en_US-ljspeech-high.onnx"
wget -q --show-progress "$HF/en/en_US/ljspeech/high/en_US-ljspeech-high.onnx.json"

# lessac high — the most widely fine-tuned female base, high quality
wget -q --show-progress "$HF/en/en_US/lessac/high/en_US-lessac-high.onnx"
wget -q --show-progress "$HF/en/en_US/lessac/high/en_US-lessac-high.onnx.json"

# hfc_female medium — noticeably more natural prosody than the older voices
wget -q --show-progress "$HF/en/en_US/hfc_female/medium/en_US-hfc_female-medium.onnx"
wget -q --show-progress "$HF/en/en_US/hfc_female/medium/en_US-hfc_female-medium.onnx.json"

# kristin medium — community-trained, pleasant and less robotic
wget -q --show-progress "$HF/en/en_US/kristin/medium/en_US-kristin-medium.onnx"
wget -q --show-progress "$HF/en/en_US/kristin/medium/en_US-kristin-medium.onnx.json"

# British options — very clear, warm
wget -q --show-progress "$HF/en/en_GB/alba/medium/en_GB-alba-medium.onnx"
wget -q --show-progress "$HF/en/en_GB/alba/medium/en_GB-alba-medium.onnx.json"
wget -q --show-progress "$HF/en/en_GB/jenny_dioco/medium/en_GB-jenny_dioco-medium.onnx"
wget -q --show-progress "$HF/en/en_GB/jenny_dioco/medium/en_GB-jenny_dioco-medium.onnx.json"
