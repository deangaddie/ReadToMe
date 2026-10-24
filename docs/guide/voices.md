# Choose the voices

Give each speaker a voice so the book sounds like its cast. A voice is either designed from a text description or cloned from a short recording. Either way it needs audio before it can read any lines.

## How

1. Choose **Open cast**. For the whole cast at once, choose **Generate voice prompts**: the LLM writes a voice description for every character. Then choose **Generate audio** to make each prompt voice's sample. Both run in Activity. If some characters already have voices, choose **Only characters without voices**. **Clear and regenerate all** deletes every existing voice first.
2. To shape one character's voice, open it and use **Add voice**:
   - **Prompt**: write or edit the **Voice description prompt** (or **Regenerate with AI**), choose **Save prompt**, then **Generate audio**. Listen, and regenerate until it fits.
   - **Reference audio**: upload a clean recording of 30 s or less. 15 s or less clones best. Under **Transcript**, **Send to AI** writes down what the recording says, which helps some providers clone it. **Edit audio** offers clean-up steps such as **Denoise** and **Silence trim**. Preview a step before you **Apply** it.
3. Mark the voice to use with the star (**Set as default**).
4. When a character's voice should change partway through the book, for example a child who grows up, add a second voice and use **Add rule**. Pick the voice, an anchor (volume, part, chapter, paragraph or line), and **From here on** or **Just this node**. **Resolved voice per chapter** shows the result. A rule whose anchor was deleted by a reread or a structure edit is skipped and flagged.

Changing a voice does not regenerate lines already spoken in it.

## Done when

Every speaker with lines has a voice with audio. The Voices row reads **Done**.
