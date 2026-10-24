# Generate the audio

Synthesise every paragraph and check the lines that came out wrong. Each item is spoken in its speaker's voice, then transcribed and compared with the text so mistakes are flagged for you.

## How

1. Choose **Generate needed audio**. It queues every item that has a speaker but no audio. Progress, rate and ETA show in Activity. To make audio for one chapter, use **Generate audio for this node** on its menu in **Audio** mode. For chosen items, tick them and use **Generate audio**. Generating over existing audio replaces it.
2. Choose **Open in Audio mode** to listen. The tree marks chapters with paragraphs needing audio or audio to review.
3. Work through the flagged items:
   - **Verify failed**: the transcript differs too much from the text. Hover the chip to see the word error rate. Listen, then use **Generate audio** for another take, or **Dismiss** if it sounds right.
   - **Normalize failed**: post-processing failed. Check the ffmpeg path.
   - **Failed** or **Unfinished**: generation itself failed. Use **Retry**.
4. Spot-check unflagged lines too. Verification catches wrong words, not a wrong tone.

Editing text, a delivery cue, a pronunciation or a speaker discards the affected audio. Run **Generate needed audio** again afterwards.

The result depends on Settings: the active **Paragraph TTS** provider, and under **Audio processing** the ffmpeg path, **Silence trim**, **Consonant soften**, the **WER threshold** (0.15 by default) and **Max audio attempts** (1, so no automatic retry).

## Done when

No paragraphs are missing audio and the Audio row reads **Done**.
