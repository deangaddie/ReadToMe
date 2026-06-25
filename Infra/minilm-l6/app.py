from fastapi import FastAPI
from pydantic import BaseModel
from sentence_transformers import SentenceTransformer, util
import os

app = FastAPI()

cache_dir = os.environ.get("SENTENCE_TRANSFORMERS_HOME", "/cache/sentence-transformers")
model = SentenceTransformer("sentence-transformers/all-MiniLM-L6-v2", cache_folder=cache_dir)

class TextPair(BaseModel):
    text1: str
    text2: str

@app.post("/similarity")
def similarity(payload: TextPair):
    emb1 = model.encode(payload.text1, convert_to_tensor=True)
    emb2 = model.encode(payload.text2, convert_to_tensor=True)
    score = util.cos_sim(emb1, emb2).item()
    return {"similarity": score}
