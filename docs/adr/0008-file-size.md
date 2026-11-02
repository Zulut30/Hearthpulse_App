# Hand-written source files stay at 600 lines or fewer; exceptions are listed here

A file over 600 lines in `src/` or `tools/` usually holds more than one responsibility, and it is where merge
conflicts between parallel sessions concentrate (`GameSession`, `HistoryStore`, the hosts). The limit is one of the
"Etalon" criteria (roadmap 2026-11-01, addendum 2026-11-02). Main and overlay windows aim lower, at 400 lines of markup,
once their repeated trees become controls of the same shape in both UIs (package Q5).

Splitting follows responsibility, not line count: a partial class per concern is fine only when each part has one
concern and a name that says it (`GameSession.Replay.cs`).

## Exceptions

| File | Why |
|---|---|
| `*.g.cs` | Generated tables (`tools/`), regenerated rather than edited |

Packages Q4 (Core giants) and Q5 (markup) add the files they decide to keep whole, with the reason. `tests/` files are
split when touched; the limit is not enforced there.
