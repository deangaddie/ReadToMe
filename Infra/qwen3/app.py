import asyncio
import ctypes
import io
import logging
import sys
import threading
from typing import Optional

import torch
from fastapi import FastAPI, Form, HTTPException, Request
from fastapi.responses import Response
import soundfile as sf
from qwen_tts import Qwen3TTSModel

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="Qwen3 TTS")

device = "cuda" if torch.cuda.is_available() else "cpu"
device_map = "cuda:0" if device == "cuda" else "cpu"
MODEL_NAME = "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign"

logger.info("Loading Qwen3 TTS model %s on %s", MODEL_NAME, device)
try:
    tts = Qwen3TTSModel.from_pretrained(
        MODEL_NAME,
        device_map=device_map,
        dtype=torch.bfloat16,
    )
except Exception as ex:
    logger.exception("Failed to load Qwen3 TTS model %s", MODEL_NAME)
    raise

logger.info("Qwen3 TTS ready")

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


def _generate(text: str, voice_description: str, language: str, gen_kwargs: dict) -> bytes:
    wavs, sr = tts.generate_voice_design(
        text=text,
        instruct=voice_description,
        language=_LANG_MAP.get(language, language),
        **gen_kwargs,
    )
    # wavs is a list of numpy arrays, take the first one
    buf = io.BytesIO()
    sf.write(buf, wavs[0], sr, format="wav")
    buf.seek(0)
    return buf.read()


@app.post("/tts")
async def synthesize(
    request: Request,
    text: str = Form(...),
    voice_description: str = Form(...),
    language: str = Form("auto"),
    temperature: Optional[float] = Form(None),
    top_p: Optional[float] = Form(None),
    top_k: Optional[int] = Form(None),
    repetition_penalty: Optional[float] = Form(None),
    max_new_tokens: Optional[int] = Form(None),
):
    """
    Generate speech using Qwen3 TTS.

    - text: plain text to synthesize
    - voice_description: text description of the desired voice (e.g. "young female with British accent")
    - language: language code or "auto" (auto/en/zh/de/it/pt/es/ja/ko/fr/ru)
    - temperature, top_p, top_k, repetition_penalty, max_new_tokens: optional HF sampling kwargs
    """
    if not text.strip():
        raise HTTPException(status_code=422, detail="text must not be empty")
    if not voice_description.strip():
        raise HTTPException(status_code=422, detail="voice_description must not be empty")

    logger.info("Qwen3 TTS: voice description '%s', language '%s'", voice_description, language)

    gen_kwargs = {
        k: v for k, v in {
            "temperature": temperature,
            "top_p": top_p,
            "top_k": top_k,
            "repetition_penalty": repetition_penalty,
            "max_new_tokens": max_new_tokens,
        }.items() if v is not None
    }

    async with _gen_lock:
        wav_bytes = await _run_with_cancel(
            request, lambda: _generate(text, voice_description, language, gen_kwargs)
        )

    return _wav_response(wav_bytes)
