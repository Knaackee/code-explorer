# CodeExplorer – Ablaufplan / Flow Plan

## Braucht man ein LLM?

**Nein.** CodeExplorer ist eine reine Retrieval-Engine. Es enthält **kein** LLM und benötigt **keinen** Netzwerkzugang.

Die Idee: Ein LLM, das CodeExplorer nutzt, bekommt genau die Symbole die es braucht – effizient und token-sparend. Das LLM ist der Konsument, nicht Teil der Library.

| Feature              | Implementierung                               |
|----------------------|-----------------------------------------------|
| **Zusammenfassung**  | `SignatureFallbackSummarizer` – extrahiert Docstrings bzw. die Signatur (kein Netzwerk) |
| **Suche**            | BM25 + Fuzzy (Reciprocal Rank Fusion) – rein lokal |

Alle Features – Parsing, Keyword-Suche, Fuzzy-Suche, PageRank, Dead-Code-Erkennung, Blast-Radius, Git-Diff-Analyse, Outline, File-Tree – laufen **immer lokal und ohne LLM**.

---

## Kompletter Ablauf

### Phase 1 – Indexierung (`CodeIndexer`)

```
Eingabe: GitHub-Repo (owner/repo) oder lokaler Ordner (Pfad)
                          │
                          ▼
              ┌───────────────────────┐
              │  1. Quelle auflösen   │
              │  ─────────────────    │
              │  GitHub → OctokitRepositoryClient │
              │  Lokal  → LocalFolderClient       │
              └───────────┬───────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  2. .gitignore laden  │  ← nur bei lokalem Ordner
              │  (DefaultSecurityFilter│
              │   + Ignore-Bibliothek)│
              └───────────┬───────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  3. Dateiliste holen  │
              │  GetFilesAsync()      │
              │  ─────────────────    │
              │  • Sprache erkennen   │  ← DefaultLanguageDetector (Dateiendung)
              │  • SecurityFilter     │  ← Binaries, Secrets, zu große Dateien,
              │    prüfen             │    .gitignore, übersprungene Verzeichnisse
              └───────────┬───────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  4. Pro Datei:        │  (max. 4 parallel via SemaphoreSlim)
              │  ProcessFileAsync()   │
              │  ─────────────────    │
              │  a) Quellcode lesen   │
              │  b) SHA-256-Hash      │  ← Inkrementell: unveränderte Dateien
              │     berechnen         │    überspringen
              │  c) Raw Source        │
              │     speichern         │  ← für O(1) Byte-Offset-Retrieval
              │  d) LanguageSpec      │
              │     nachschlagen      │  ← LanguageRegistry.ForFile()
              │  e) Symbole           │
              │     extrahieren       │  ← SymbolExtractor (siehe Phase 1a)
              └───────────┬───────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  5. Zusammenfassungen │
              │  SummarizeSymbolsAsync│
              │  ─────────────────    │
              │  SignatureFallback    │  ← Docstring oder Signatur
              │  Summarizer           │    (kein Netzwerk)
              └───────────┬───────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  6. PageRank          │  ← Kein LLM
              │  berechnen            │
              │  ─────────────────    │
              │  Dependency-Graph     │
              │  aus Symbol.References│
              │  → Power-Iteration    │
              │  → CentralityScore    │
              └───────────┬───────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  8. Index speichern   │  ← JsonFileIndexStore
              │  SaveAsync()          │    (oder eigener IIndexStore)
              └───────────────────────┘
```

### Phase 1a – Symbol-Extraktion (`SymbolExtractor`)

```
Quellcode + Sprache + LanguageSpec
              │
              ├──→ Tree-Sitter vorhanden?
              │         │
              │    Ja   ▼
              │    ┌──────────────────────┐
              │    │  Tree-Sitter-Parser  │  ← Kein LLM
              │    │  AST aufbauen        │
              │    │  WalkNode() rekursiv │
              │    │  → Klassen, Methoden,│
              │    │    Funktionen, etc.  │
              │    │  → Docstrings        │
              │    │    extrahieren        │
              │    └──────────────────────┘
              │
              └──→ Nein (Fallback)
                        │
                        ▼
                   ┌──────────────────────┐
                   │  Regex-Heuristik     │  ← Kein LLM
                   │  LanguageSpec.Patterns│
                   │  → Grobe Symbol-     │
                   │    Erkennung          │
                   └──────────────────────┘
                        │
                        ▼
              ┌───────────────────────┐
              │  AssignParents()      │  ← Eltern-Kind-Beziehung
              │  über Zeilenbereiche  │    anhand von Zeilenbereichen
              └───────────────────────┘
```

---

### Phase 2 – Suche & Retrieval (`SymbolRetriever`, `HybridSearchEngine`)

```
Suchanfrage (Query)
              │
              ▼
   ┌──────────────────────────────────────────────┐
   │  HybridSearchEngine.SearchAsync()            │
   │  ────────────────────────────────            │
   │                                              │
   │  ┌─────────────┐   ┌──────────────┐         │
   │  │  BM25Engine  │   │ FuzzySearch  │         │
   │  │  (TF-IDF     │   │ (Levenshtein │         │
   │  │   Ranking)    │   │  Ratio)      │         │
   │  └──────┬───────┘   └──────┬───────┘         │
   │         │                  │                  │
   │         ▼                  ▼                  │
   │  ┌────────────────────────────────┐           │
   │  │  Reciprocal Rank Fusion (RRF)  │           │
   │  │  Gewichte: BM25=1.0            │           │
   │  │           Fuzzy=0.5            │           │
   │  └──────────────┬────────────────┘           │
   └─────────────────┼────────────────────────────┘
                     │
                     ▼
          ┌─────────────────────┐
          │  Top-K Ergebnisse   │
          │  (SearchResult[])   │
          └──────────┬──────────┘
                     │
                     ▼
          ┌─────────────────────┐
          │  GetRankedContext    │  ← Quellcode per Byte-Offset laden
          │  Async()            │    bis Token-Budget erreicht
          │  → ContextBundle    │
          └─────────────────────┘
```

Zusätzlich: **TextSearchEngine** für reine Volltext-Suche im Raw Source (kein LLM).

---

### Phase 3 – Analyse (kein LLM benötigt)

```
┌───────────────────────────┐
│  PageRankCalculator       │  Power-Iteration auf dem Dependency-Graph
│  → CentralityScore        │  → Wichtigste Symbole identifizieren
├───────────────────────────┤
│  DeadCodeDetector         │  Symbole ohne eingehende Referenzen
│  → DeadSymbol[]           │  (Klassen/Interfaces ausgenommen)
├───────────────────────────┤
│  BlastRadiusCalculator    │  BFS über den Referenz-Graph
│  → Symbol[] (betroffene)  │  → Welche Symbole sind bei Änderung betroffen?
├───────────────────────────┤
│  GitDiffAnalyzer          │  LibGit2Sharp: zwei Commits vergleichen
│  → ChangedSymbols         │  → Added / Modified / Deleted Symbole
└───────────────────────────┘
```

---

### Phase 4 – Outline & Navigation (kein LLM benötigt)

```
OutlineProvider
  │
  ├── GetRepoOutlineAsync()   → Hierarchischer Symbolbaum (SymbolNode[])
  ├── GetFileOutlineAsync()   → Symbolbaum einer einzelnen Datei
  └── GetFileTreeAsync()      → Liste aller indizierten Dateipfade
```

---

### Phase 5 – Live-Watching (kein LLM benötigt)

```
FolderWatcher
  │
  │  FileSystemWatcher auf dem Ordner
  │  → Änderung erkannt (Created / Changed / Deleted / Renamed)
  │  → 2-Sekunden-Debounce
  │
  └──→ Callback (z.B. ReIndexAsync)
```

---

## Konfiguration

```csharp
services.AddCodeExplorer();
// → SignatureFallbackSummarizer (Docstrings + Signaturen)
// → BM25 + Fuzzy Search (Hybrid mit RRF)
// → Alles lokal, kein Netzwerk (außer GitHub bei Remote-Repos)
// → Kein LLM, kein API-Key erforderlich
```

Mit Options:

```csharp
services.AddCodeExplorer(opt =>
{
    opt.MaxFilesPerRepo = 5000;
});
```

---

## Zusammenfassung

```
┌────────────────────────────────────────────────────────────────┐
│               Reines Retrieval-System – Kein LLM              │
│                                                               │
│  Parsing (Tree-Sitter / Regex)                                │
│  Keyword-Suche (BM25)                                         │
│  Fuzzy-Suche (Levenshtein)                                    │
│  Hybrid-Suche (Reciprocal Rank Fusion)                        │
│  Inkrementelle Indexierung (SHA-256 Hash)                     │
│  Signatur-basierte Zusammenfassungen (Docstrings + Signaturen)│
│  PageRank-Analyse                                             │
│  Dead-Code-Erkennung                                          │
│  Blast-Radius-Berechnung                                      │
│  Git-Diff-Analyse                                             │
│  Outline / File-Tree                                          │
│  Security-Filter / .gitignore                                 │
│  Live-Watching (FileSystemWatcher)                            │
│  JSON-Persistenz                                              │
└────────────────────────────────────────────────────────────────┘
```
