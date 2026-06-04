# ═══════════════════════════════════════════════════
# Stage 1: Build C#
# ═══════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src
COPY src/TolkTtsBot/TolkTtsBot.csproj ./
RUN dotnet restore
COPY src/TolkTtsBot/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# Проверяем что wwwroot попал в publish
RUN ls -la /app/publish/wwwroot/ && echo "✓ wwwroot OK"

# ═══════════════════════════════════════════════════
# Stage 2: Runtime (Debian 12 Bookworm)
# ═══════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# Базовые пакеты
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip python3-venv \
    pulseaudio pulseaudio-utils \
    supervisor curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Chromium (тянет свои зависимости автоматически)
RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Python venv + Silero sidecar
RUN python3 -m venv /app/venv
COPY tts-sidecar/requirements.txt ./tts-sidecar/
RUN /app/venv/bin/pip install --upgrade pip && \
    /app/venv/bin/pip install --no-cache-dir -r tts-sidecar/requirements.txt
COPY tts-sidecar/main.py ./tts-sidecar/

# .NET приложение (wwwroot включён в publish)
COPY --from=build /app/publish /app/dotnet

# Проверяем wwwroot в финальном образе
RUN ls -la /app/dotnet/wwwroot/ && echo "✓ wwwroot в runtime образе OK"

# Playwright использует системный Chromium
ENV PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium

# Docker конфиги
COPY docker/supervisord.conf /etc/supervisor/conf.d/supervisord.conf
COPY docker/pulse-default.pa /etc/pulse/default.pa
COPY docker/entrypoint.sh    /entrypoint.sh
RUN chmod +x /entrypoint.sh

RUN mkdir -p /app/models /app/logs

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_ENVIRONMENT=Production \
    TTS_SIDECAR_URL=http://localhost:8765 \
    MODEL_DIR=/app/models \
    TTS_VOICE=xenia \
    BOT_NAME="TTS Бот"

EXPOSE 10000
ENTRYPOINT ["/entrypoint.sh"]
