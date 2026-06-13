import asyncio
import ctypes
import gc
import io
import logging
import sys
import tempfile
import threading
import os
from typing import Optional

# torchvision conflicts with PyTorch 2.3.0 in this image — block it before
# transformers loads so its lazy importer skips the vision code path entirely.
sys.modules["torchvision"] = None  # type: ignore[assignment]

import torch
import torchaudio
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import Response
from chatterbox.tts_turbo import ChatterboxTurboTTS

torch.backends.cuda.matmul.allow_tf32 = True
torch.backends.cudnn.benchmark = True

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="Chatterbox Turbo TTS")

device = "cuda" if torch.cuda.is_available() else "cpu"

logger.info("Loading ChatterboxTurboTTS on %s", device)
model_turbo = ChatterboxTurboTTS.from_pretrained(device=device)
logger.info("ChatterboxTurboTTS ready")

# Semaphore: one generation at a time (GPU is single-tenant).
_gen_lock = asyncio.Lock()


@app.get("/health")
def health():
    return {"status": "ok", "device": device}


async def _save_upload(upload: UploadFile) -> tuple[bytes, str]:
    """Write uploaded audio to a temp file. Returns (data, tmp_path)."""
    data = await upload.read()
    suffix = os.path.splitext(upload.filename or "ref.wav")[1] or ".wav"
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as f:
        f.write(data)
        return data, f.name


def _wav_response(wav: torch.Tensor, sample_rate: int) -> Response:
    buf = io.BytesIO()
    torchaudio.save(buf, wav, sample_rate, format="wav")
    buf.seek(0)
    return Response(content=buf.read(), media_type="audio/wav")


def _raise_in_thread(tid: int, exc_type: type) -> bool:
    """Inject an exception into a running thread via CPython internals."""
    res = ctypes.pythonapi.PyThreadState_SetAsyncExc(
        ctypes.c_ulong(tid), ctypes.py_object(exc_type)
    )
    return res == 1


async def _run_with_cancel(request: Request, fn):
    """
    Run blocking fn() in a thread executor.
    If the client disconnects mid-generation, inject KeyboardInterrupt into
    the worker thread so PyTorch unwinds, then raise HTTPException 499.
    """
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

    # Poll disconnect while generation runs.
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

    await future  # propagate executor exceptions

    if error:
        raise error[0]
    return result[0]


@app.post("/tts/turbo")
async def synthesize_turbo(
    request: Request,
    text: str = Form(...),
    reference_audio: UploadFile = File(...),
):
    """
    Generate speech using ChatterboxTurboTTS.

    Use this endpoint when text contains paralinguistic tags.
    Supported tags: [laugh] [chuckle] [sigh] [cough] [clear throat] [gasp] [groan] [sniff] [shush]

    - text: text with optional paralinguistic tags
    - reference_audio: WAV/MP3 for voice cloning, ~10 seconds ideal (required)

    Note: exaggeration and cfg_weight are not supported by the turbo model.
    """
    if not text.strip():
        raise HTTPException(status_code=422, detail="text must not be empty")

    tmp_ref: Optional[str] = None
    try:
        data, tmp_ref = await _save_upload(reference_audio)
        logger.info("TTS turbo: voice clone %d bytes", len(data))

        async with _gen_lock:
            wav = await _run_with_cancel(
                request,
                lambda: model_turbo.generate(text, audio_prompt_path=tmp_ref),
            )
            response = _wav_response(wav, model_turbo.sr)
            del wav
            gc.collect()
            torch.cuda.empty_cache()
        return response
    finally:
        if tmp_ref and os.path.exists(tmp_ref):
            os.unlink(tmp_ref)
