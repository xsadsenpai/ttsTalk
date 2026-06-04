#!/bin/bash
set -e

echo "=== TolkTtsBot entrypoint ==="

# Render задаёт PORT через env. Дефолт 10000 (Render default для Docker).
export PORT="${PORT:-10000}"
echo "PORT=$PORT"

# PulseAudio null-sink для headless аудио
export PULSE_RUNTIME_PATH=/run/pulse
mkdir -p "$PULSE_RUNTIME_PATH"
if command -v pulseaudio &>/dev/null; then
    pulseaudio --start --daemon --system=false \
        --exit-idle-time=-1 --disallow-exit --log-level=warn \
        --load="module-null-sink sink_name=virtual_speaker" 2>/dev/null || true
    sleep 1
fi

echo "Chromium: $(which chromium 2>/dev/null || echo 'not found')"
echo "Запуск supervisor..."
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
