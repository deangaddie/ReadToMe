#!/usr/bin/env python3
"""Native VoxCPM2 inference server — direct model, no vLLM backend."""
from __future__ import annotations

import asyncio
import json
import logging
import os
import struct
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

import numpy as np
import torch
import soundfile as sf
from fastapi import FastAPI, File, HTTPException, Request, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse

logger = logging.getLogger("voxcpm2")
logging.basicConfig(level=logging.INFO)

_UPLOAD_DIR = Path(os.environ.get("UPLOAD_DIR", "/uploads"))
_UPLOAD_DIR.mkdir(parents=True, exist_ok=True)

_MAX_UPLOAD_BYTES = int(os.environ.get("MAX_UPLOAD_BYTES", 50 * 1024 * 1024))
_UPLOAD_TTL_SECONDS = int(os.environ.get("UPLOAD_TTL_SECONDS", 3600))
_ALLOWED_AUDIO_EXTENSIONS = {".wav", ".mp3", ".flac", ".ogg", ".m4a"}

_HF_HOME = os.environ.get("HF_HOME", "/cache")

_model = None
_upload_registry: dict[str, Path] = {}
_cleanup_task = None


@asynccontextmanager
async def _lifespan(app: FastAPI):
    global _model, _cleanup_task
    logger.info("Loading VoxCPM2 model...")
    from voxcpm import VoxCPM
    model_id = os.environ.get("MODEL_ID", "openbmb/VoxCPM2")
    optimize = os.environ.get("OPTIMIZE", "false").lower() == "true"
    _model = VoxCPM.from_pretrained(
        model_id, optimize=optimize, load_denoiser=False
    )
    logger.info("VoxCPM2 model loaded.")
    _cleanup_task = asyncio.create_task(_upload_cleanup_loop())
    yield
    if _cleanup_task:
        _cleanup_task.cancel()


async def _upload_cleanup_loop():
    while True:
        await asyncio.sleep(300)
        now = time.time()
        expired = [
            fid for fid, fpath in list(_upload_registry.items())
            if fpath.exists() and (now - fpath.stat().st_mtime) > _UPLOAD_TTL_SECONDS
        ]
        for fid in expired:
            _upload_registry.pop(fid, None).unlink(missing_ok=True)
        if expired:
            logger.info("Cleaned %d expired uploads", len(expired))


app = FastAPI(title="VoxCPM2 Native Server", lifespan=_lifespan)
app.add_middleware(
    CORSMiddleware,
    allow_origins=os.environ.get("CORS_ORIGINS", "*").split(","),
    allow_methods=["GET", "POST"],
    allow_headers=["Content-Type"],
)


def _json_frame(obj: dict) -> bytes:
    payload = json.dumps(obj).encode()
    return b"\x00" + struct.pack("<I", len(payload)) + payload


def _audio_frame(data: bytes) -> bytes:
    return b"\x01" + struct.pack("<I", len(data)) + data


@app.get("/health")
async def health():
    return {"status": "ok", "model_loaded": _model is not None}


@app.post("/upload-audio")
async def upload_audio(file: UploadFile = File(...)):
    filename = file.filename or "audio.wav"
    suffix = Path(filename).suffix.lower()
    if suffix not in _ALLOWED_AUDIO_EXTENSIONS:
        raise HTTPException(400, f"Unsupported audio format: {suffix}")

    fid = str(uuid.uuid4())
    dest = _UPLOAD_DIR / f"{fid}{suffix}"

    size = 0
    with dest.open("wb") as f:
        while chunk := await file.read(64 * 1024):
            size += len(chunk)
            if size > _MAX_UPLOAD_BYTES:
                dest.unlink(missing_ok=True)
                raise HTTPException(413, "File too large")
            f.write(chunk)

    _upload_registry[fid] = dest.resolve()
    return {"file_id": fid}


@app.post("/api/stream")
async def api_stream(request: Request):
    try:
        params = await request.json()
    except Exception as exc:
        async def _err():
            yield _json_frame({"type": "error", "message": f"invalid request: {exc}"})
        return StreamingResponse(_err(), media_type="application/octet-stream")

    async def generate():
        if _model is None:
            yield _json_frame({"type": "error", "message": "model not loaded"})
            return

        text = (params.get("text") or "").strip()
        if not text:
            yield _json_frame({"type": "error", "message": "text is required"})
            return

        control = (params.get("control") or "").strip()
        if control:
            text = f"({control}){text}"

        ref_id = (params.get("reference_wav_path") or "").strip()
        ref_path = str(_upload_registry[ref_id]) if ref_id and ref_id in _upload_registry else None

        cfg_value = float(params.get("cfg_value", 2.0))
        inference_timesteps = int(params.get("inference_timesteps", 10))
        min_len = int(params.get("min_len", 2))
        max_len = int(params.get("max_len", 4096))
        normalize = bool(params.get("normalize", False))
        denoise = bool(params.get("denoise", False))
        retry_badcase = bool(params.get("retry_badcase", True))
        retry_badcase_max_times = int(params.get("retry_badcase_max_times", 3))
        retry_badcase_ratio_threshold = float(params.get("retry_badcase_ratio_threshold", 6.0))

        try:
            audio_np, sample_rate = await asyncio.to_thread(
                _run_generate,
                text, ref_path,
                cfg_value, inference_timesteps, min_len, max_len,
                normalize, denoise,
                retry_badcase, retry_badcase_max_times, retry_badcase_ratio_threshold,
            )
        except Exception as exc:
            logger.exception("Generation error")
            yield _json_frame({"type": "error", "message": str(exc)})
            return

        yield _json_frame({"type": "meta", "sample_rate": sample_rate})

        float32_bytes = audio_np.astype(np.float32).tobytes()
        chunk_size = 4096 * 4  # 4096 float32 samples
        for i in range(0, len(float32_bytes), chunk_size):
            yield _audio_frame(float32_bytes[i:i + chunk_size])

        yield _json_frame({"type": "done", "chunks": (len(float32_bytes) + chunk_size - 1) // chunk_size})

    return StreamingResponse(generate(), media_type="application/octet-stream")


def _run_generate(
    text: str,
    reference_wav_path: str | None,
    cfg_value: float,
    inference_timesteps: int,
    min_len: int,
    max_len: int,
    normalize: bool,
    denoise: bool,
    retry_badcase: bool,
    retry_badcase_max_times: int,
    retry_badcase_ratio_threshold: float,
) -> tuple[np.ndarray, int]:
    try:
        audio = _model.generate(
            text=text,
            reference_wav_path=reference_wav_path,
            cfg_value=cfg_value,
            inference_timesteps=inference_timesteps,
            min_len=min_len,
            max_len=max_len,
            normalize=normalize,
            denoise=denoise,
            retry_badcase=retry_badcase,
            retry_badcase_max_times=retry_badcase_max_times,
            retry_badcase_ratio_threshold=retry_badcase_ratio_threshold,
        )
        sample_rate = _model.tts_model.sample_rate
        return np.asarray(audio, dtype=np.float32), sample_rate
    finally:
        torch.cuda.empty_cache()
