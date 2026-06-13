# Data model

## Ordering in the project

Tables will all have unique persistent Guid Ids. They will be ordered by a column with fractional indexing. The column has to be forced to be case sensitive, and we will use the Base-62 ordering (a-zA-Z0-9). The nuget package will be used to handle the ordering values: https://www-0.nuget.org/packages/FractionalIndexing

Every book will have at least one volume and one part and one chapter. If they are singular, the UI will just not display them, but the data will always have them.

ParagraphItem: Paragraphs consist of items. If the paragraph has no character speach or is only character speach, the whole paragraph will just be the single line. If there is a mixture of narration and character speach, the paragraph will have to be split into lines switching between the narrator and the character parts with type Narration or Type Character. In addition to the lines, a paragraph may have items other than lines - volume-, part-, chapter-, paragraph- breaks. These are special items to help create pauses in the correct places in the final audio. It may be just a Pause for if the user wants a pause other than the named and auto-assigned pauses above.

A special Narrator character will be created for every project. It will not be deletable. Narration lines will be assigned to the narrator by convention.

## Tables

### Project

The project table contains the project details

- Project title (max length 250) (not null)
- Book title (max length 250) (not null)
- Author (max length 250) (not null)
- Filename (max length 526) (not null)
- Type (epub or text) (not null)

### Volume

- Id (Guid) PK (not null)
- Volume title (max length 250) (not null)
- Order (Case sensitive!) (max length 250) (not null)

### Part

- Id (Guid) PK (not null)
- VolumeId (Guid) FK (not null)
- Part title (max length 250) (nullable)
- Order (Case sensitive) (max length 250) (not null)

### Chapter

- Id (Guid) PK (not null)
- PartId (Guid) FK (not null)
- Chapter title (max length 250) (nullable)
- Order (Case sensitive) (max length 250) (not null)

### Paragraph

- Id (Guid) PK (not null)
- ChapterId (guid) FK (not null)
- Order (Case sensitive) (max length 250) (not null)
- CharacterId (Guid)

### ParagraphItem

- Id (Guid) PK (not null)
- ParagraphId (guid) FK (not null)
- Order (Case sensitive) (max length 250) (not null)
- ItemType (Narration, Character, VolumePause, PartPause, ChapterPause, ParagraphPause, Pause)
- CharacterId (Guid) FK (Nullable - starts off with the narrator's ID or null (Unknown) until the LLM or user sets the speaking character.)
- VoiceInstructions (max length 3000, JSON schema TBD)

### Character (todo: Complete columns as needed)

- Id (Guid) PK (not null)
- Name (max length 250)
- IsNarrator (boolean) (True for the special narrator character, otherwise false)

### Voice (todo: Complete columns as needed)

- Id (Guid) PK (not null)
- CharacterId (Guid) FK
- Title (max length 250)
