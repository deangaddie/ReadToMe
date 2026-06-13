import asyncio
import ctypes
import io
import logging
import sys
import threading
from typing import Optional

import torch
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import Response
import soundfile as sf
from qwen_tts import Qwen3TTSModel

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="Qwen3 TTS Base")

device = "cuda" if torch.cuda.is_available() else "cpu"
device_map = "cuda:0" if device == "cuda" else "cpu"
MODEL_NAME = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"

logger.info("Loading Qwen3 TTS Base model %s on %s", MODEL_NAME, device)
try:
    tts = Qwen3TTSModel.from_pretrained(
        MODEL_NAME,
        device_map=device_map,
        dtype=torch.bfloat16,
    )
except Exception as ex:
    logger.exception("Failed to load Qwen3 TTS Base model %s", MODEL_NAME)
    raise

logger.info("Qwen3 TTS Base ready")

_gen_lock = asyncio.Lock()


@app.get("/health")
def health():
    return {"status": "ok", "device": device, "model": MODEL_NAME}


def _wav_response(wav_bytes: bytes) -> Response:
    return Response(content=wav_bytes, media_type="audio/wav")


def _raise_in_thread(tid: int, exc_type: type) -> bool:
    res = ctypes.pythonapi.PyThreadState_SetAsyncExc(
        ctypes.c_ulong(tid), ctypes.py_object(exc_type)
    )
    return res == 1


async def _run_with_cancel(request: Request, fn):
    loop = asyncio.get_running_loop()
    result: list = []
    error: list = []
    worker_tid: list[int] = []

    def _worker():
        worker_tid.append(threading.current_thread().ident)
        try:
            result.append(fn())
        except BaseException as e:
            error.append(e)

    future = loop.run_in_executor(None, _worker)

    while not future.done():
        if await request.is_disconnected():
            logger.warning("Client disconnected — cancelling generation")
            if worker_tid:
                _raise_in_thread(worker_tid[0], KeyboardInterrupt)
            try:
                await asyncio.wait_for(future, timeout=5.0)
            except (asyncio.TimeoutError, KeyboardInterrupt, Exception):
                pass
            raise HTTPException(status_code=499, detail="Client disconnected")
        await asyncio.sleep(0.5)

    await future
    if error:
        raise error[0]
    return result[0]


_LANG_MAP = {
    "auto": "auto", "en": "english", "zh": "chinese", "de": "german",
    "it": "italian", "pt": "portuguese", "es": "spanish", "ja": "japanese",
    "ko": "korean", "fr": "french", "ru": "russian",
}

def _generate(text: str, reference_audio_bytes: bytes, voice_transcript: str, language: str = "auto") -> bytes:
    import numpy as np
    audio_array, sr = sf.read(io.BytesIO(reference_audio_bytes))
    if audio_array.ndim > 1:
        audio_array = np.mean(audio_array, axis=-1).astype(np.float32)
    else:
        audio_array = audio_array.astype(np.float32)
    lang = _LANG_MAP.get(language, language)
    wavs, out_sr = tts.generate_voice_clone(
        text=text,
        language=lang,
        ref_audio=(audio_array, sr),
        ref_text=voice_transcript,
    )
    buf = io.BytesIO()
    sf.write(buf, wavs[0], out_sr, format="wav")
    buf.seek(0)
    return buf.read()


@app.post("/tts")
async def synthesize(
    request: Request,
    text: str = Form(...),
    reference_audio: UploadFile = File(...),
    voice_transcript: str = Form(...),
    language: str = Form("auto"),
):
    """
    Generate speech using Qwen3 TTS Base model (voice cloning).

    - text: plain text to synthesize
    - reference_audio: WAV/MP3 voice sample file
    - voice_transcript: transcript of the reference audio
    - language: language code or "auto" (auto/en/zh/de/it/pt/es/ja/ko/fr/ru)
    """
    if not text.strip():
        raise HTTPException(status_code=422, detail="text must not be empty")
    if not voice_transcript.strip():
        raise HTTPException(status_code=422, detail="voice_transcript must not be empty")

    audio_bytes = await reference_audio.read()
    if not audio_bytes:
        raise HTTPException(status_code=422, detail="reference_audio must not be empty")

    logger.info("Qwen3 TTS Base: language '%s', transcript len=%d", language, len(voice_transcript))

    async with _gen_lock:
        wav_bytes = await _run_with_cancel(
            request,
            lambda: _generate(text, audio_bytes, voice_transcript, language)
        )

    return _wav_response(wav_bytes)
