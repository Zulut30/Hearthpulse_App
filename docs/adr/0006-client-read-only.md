# The game client is only read: Power.log first, memory reads for what the log lacks, never writes or input

Game state comes from Hearthstone's Power.log. Values the log does not carry (ranks, the Arena draft offer,
Battlegrounds lobby names for the MMR option, button costs) are read from the client's memory through a handle opened
for `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION` only (`src/Hearthpulse.Core/Memory/ProcessMemory.cs`). The
app never writes to the client's memory, injects code, hooks the client or sends input to it: any of those would put
the app among the tools that risk players' accounts.

## Consequences

- A change that touches game memory, input or adds a new read goes through the `hearthpulse-legal` check before merge.
- Memory values are optional: when the client moves its fields, the read returns null and the feature degrades; the
  tracker never depends on memory for game state the log has.
- A memory read is still subject to [0005](0005-hidden-information.md): reading a value does not make it showable.
