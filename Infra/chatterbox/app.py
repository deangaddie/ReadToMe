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
from chatterbox.tts import ChatterboxTTS

torch.backends.cuda.matmul.allow_tf32 = True
torch.backends.cudnn.benchmark = True

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="Chatterbox TTS")

device = "cuda" if torch.cuda.is_available() else "cpu"

logger.info("Loading ChatterboxTTS on %s", device)
model = ChatterboxTTS.from_pretrained(device=device)
logger.info("ChatterboxTTS ready")

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


@app.post("/tts")
async def synthesize(
    request: Request,
    text: str = Form(...),
    reference_audio: UploadFile = File(...),
    instructions: Optional[str] = Form(None),
    exaggeration: float = Form(0.5),
    cfg_weight: float = Form(0.5),
):
    """
    Generate speech using ChatterboxTTS (standard model).

    - text: plain text; do NOT include paralinguistic tags here — use /tts/turbo for those
    - reference_audio: WAV/MP3 for voice cloning (required)
    - instructions: expression instructions e.g. "speak slowly and sadly"
    - exaggeration: 0–1, controls expressiveness (default 0.5)
    - cfg_weight: 0–1, classifier-free guidance weight (default 0.5)
    """
    if not text.strip():
        raise HTTPException(status_code=422, detail="text must not be empty")

    tmp_ref: Optional[str] = None
    try:
        data, tmp_ref = await _save_upload(reference_audio)
        logger.info("TTS: voice clone %d bytes, exaggeration=%.2f cfg=%.2f", len(data), exaggeration, cfg_weight)

        async with _gen_lock:
            wav = await _run_with_cancel(
                request,
                lambda: model.generate(
                    text,
                    audio_prompt_path=tmp_ref,
                    exaggeration=exaggeration,
                    cfg_weight=cfg_weight,
                ),
            )
            response = _wav_response(wav, model.sr)
            del wav
            gc.collect()
            torch.cuda.empty_cache()
        return response
    finally:
        if tmp_ref and os.path.exists(tmp_ref):
            os.unlink(tmp_ref)
