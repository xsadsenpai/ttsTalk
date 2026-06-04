"""Silero TTS sidecar — FastAPI + Silero v3.1 (ru), порт 8765."""
import io, logging, os, struct, time
from pathlib import Path

import torch
import numpy as np
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("silero")

MODEL_DIR   = Path(os.getenv("MODEL_DIR", "/app/models"))
VOICE       = os.getenv("TTS_VOICE", "xenia")
SAMPLE_RATE = int(os.getenv("TTS_SAMPLE_RATE", "48000"))

app     = FastAPI(title="Silero TTS", version="1.0")
model   = None
_speakers: list[str] = []

@app.on_event("startup")
async def load_model():
    global model, _speakers
    path = MODEL_DIR / "v3_1_ru.pt"
    if not path.exists():
        log.info("Скачиваю Silero v3.1 ru...")
        MODEL_DIR.mkdir(parents=True, exist_ok=True)
        torch.hub.download_url_to_file(
            "https://models.silero.ai/models/tts/ru/v3_1_ru.pt",
            str(path), progress=True)
    log.info("Загрузка модели...")
    t0 = time.time()
    model = torch.package.PackageImporter(str(path)).load_pickle("tts_models", "model")
    model.to("cpu")
    _speakers = list(model.speakers) if hasattr(model, "speakers") else [VOICE]
    log.info(f"Модель загружена за {time.time()-t0:.1f}с. Голоса: {_speakers}")

class SynthRequest(BaseModel):
    text: str
    voice: str = VOICE
    sample_rate: int = SAMPLE_RATE

@app.get("/health")
async def health():
    return {"status": "ok", "model_loaded": model is not None}

@app.get("/voices")
async def voices():
    return {"voices": _speakers, "current": VOICE}

@app.post("/synthesize")
async def synthesize(req: SynthRequest) -> Response:
    if model is None:
        raise HTTPException(503, "Модель не загружена")
    text = req.text.strip()[:1000]
    if not text:
        raise HTTPException(400, "Пустой текст")
    voice = req.voice if req.voice in _speakers else VOICE
    log.info(f"Синтез [{voice}]: {text[:60]}...")
    t0 = time.time()
    try:
        with torch.no_grad():
            audio = model.apply_tts(text=text, speaker=voice,
                                    sample_rate=req.sample_rate,
                                    put_accent=True, put_yo=True)
    except Exception as e:
        log.error(f"Ошибка: {e}")
        raise HTTPException(500, str(e))
    arr = audio.numpy() if isinstance(audio, torch.Tensor) else np.array(audio)
    wav = _to_wav(arr, req.sample_rate)
    log.info(f"Готово за {time.time()-t0:.2f}с — {len(wav)} байт")
    return Response(content=wav, media_type="audio/wav")

def _to_wav(audio: np.ndarray, sr: int) -> bytes:
    pcm = (np.clip(audio, -1.0, 1.0) * 32767).astype(np.int16)
    n, ch, bps = len(pcm), 1, 16
    data_sz = n * ch * bps // 8
    buf = io.BytesIO()
    buf.write(b"RIFF"); buf.write(struct.pack("<I", 36 + data_sz))
    buf.write(b"WAVE"); buf.write(b"fmt ")
    buf.write(struct.pack("<I", 16)); buf.write(struct.pack("<H", 1))
    buf.write(struct.pack("<H", ch)); buf.write(struct.pack("<I", sr))
    buf.write(struct.pack("<I", sr*ch*bps//8))
    buf.write(struct.pack("<H", ch*bps//8))
    buf.write(struct.pack("<H", bps))
    buf.write(b"data"); buf.write(struct.pack("<I", data_sz))
    buf.write(pcm.tobytes())
    return buf.getvalue()

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8765, log_level="info")
