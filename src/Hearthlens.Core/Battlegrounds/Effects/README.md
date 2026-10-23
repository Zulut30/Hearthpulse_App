# Combat effects

How minion effects plug into the Battlegrounds combat simulator (`CombatSimulator`, engine in `../Simulation/`).

Rules for every effect:

- **Clean-room.** Implement from the card text and golden text (HearthstoneJSON / hsdata) and general Battlegrounds rules.
  Do not read, run or copy another simulator's code.
- **No hidden information.** Use only what the player can see: both boards, heroes, hero powers, trinkets, the
  anomaly, and the local player's own hand. The opponent's hand and secrets never reach the simulator. An effect that
  needs a hand overrides `NeedsHand => true`; it then stays "not simulated" on the opponent's side.
- **Add files, don't edit shared ones.** One class per card (or per group of cards with the same behaviour) in its
  own file under `Effects/<Family>/`. Registration is automatic (reflection over `[CardEffect]`); there is no list to edit.

## Adding an effect

```csharp
namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>Harmless Bonehead (BG28_300): "Deathrattle: Summon two 1/1 Skeletons." Golden: four.</summary>
[CardEffect("BG28_300")]
internal sealed class HarmlessBonehead : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) =>
        fight.SummonAt(self, Tokens.Skeleton(), self.Pick(2, 4));
}
```

- `[CardEffect("<normal card id>")]` covers normal and golden; read `self.Golden` / `self.Pick(normal, golden)`
  and follow the **golden card's own text** (doubled numbers, "twice", golden tokens, ×3...). For a separate golden
  class use `[CardEffect(id, Golden = GoldenFilter.GoldenOnly)]` (or pass the golden id). Several attributes may sit on
  one class. Enchantments, hero powers, trinkets and the Deity register the same way by their card ids.
- Effect objects are shared by all fights and threads: **no mutable fields**. Per-minion state: `Unit.Counter`,
  `Unit.Counter2` (start at the captured `TAG_SCRIPT_DATA_NUM_1/2`), `Unit.AvengeCount`. Per-player state:
  `Side.Counter(key)` / `AddCounter` / `SetCounter` (start at the player entity's captured tag of that name, else 0).
- Only override the hooks you need; the engine skips effects that do not override a hook.

## Hooks (`CombatEffect`)

| Hook | When |
|---|---|
| `OnFightStart(f, self)` | once per simulated fight, before the first attack (not a Start of Combat trigger: those resolved before capture) |
| `OnAttack(f, self, target)` | self attacks, **before damage**: Rally, "whenever this attacks" |
| `OnFriendlyAttack(f, self, attacker, target)` | a friendly minion attacks, before damage (also called for self: check `attacker != self` for "another") |
| `OnFriendlyAttacked(f, self, defender, attacker)` | a friendly minion is attacked, before damage |
| `OnAttackDamage(f, self, target)` | during self's combat damage, after the main exchange: extra simultaneous damage (cleave) |
| `AfterAttack(f, self, target, outcome)` | after self attacked, before the death phase (`outcome.Killed`, `outcome.Excess`) |
| `AfterFriendlyAttack(f, self, attacker, target, outcome)` | after a friendly minion (possibly self) attacked |
| `OnDamaged(f, self, source, amount)` | self took damage (not when Divine Shield absorbed it) |
| `AfterFriendlyDealtDamage(f, self, dealer, victim, amount)` | a friendly minion dealt damage |
| `AfterKill(f, self, victim)` | self's damage or destroy killed `victim` |
| `OnFriendlyShieldLost(f, self, unit)` | a friendly minion lost Divine Shield |
| `Deathrattle(f, self)` | self died (already off the board) or `TriggerDeathrattles` was called; runs once per trigger |
| `ExtraDeathrattles(f, self)` | extra triggers for friendly deathrattles while self is alive (Titus: 1, golden 2) |
| `OnFriendlyDeathrattle(f, self, source)` | after each friendly deathrattle trigger |
| `AfterFriendlyDeath(f, self, dead)` / `AfterEnemyDeath` | after a death, once its deathrattles and Reborn resolved (Avenge: derive from `AvengeEffect`) |
| `OnSummoned(f, self)` | self was just summoned (token, reborn copy) |
| `WheneverFriendlySummoned` → `AfterFriendlySummoned(f, self, summoned)` | another friendly minion was summoned ("whenever" first) |
| `AfterFriendlyReborn(f, self, reborn)` | a friendly minion came back with Reborn (`reborn.RebornFrom` is the old one) |
| `AfterFriendlySpellCast(f, self, spellId, target)` | after `Fight.CastSpell` for this side |
| `AuraFor(f, self, other) → StatDelta` | static aura onto a friendly minion; recomputed when minions enter or leave. Captured stats already include auras present at capture; the engine only applies changes (a lost Health aura lowers Health down to the new maximum only) |
| `Refresh(f, self)` | re-check state-dependent keywords/thresholds ("Taunt while it has Divine Shield", "once this reaches 6 Attack") after stat changes and board changes |

`AvengeEffect`: override `Threshold(self)` and `Avenge(f, self)`; it counts friendly deaths while self is alive.
The counter starts at 0 each fight (start-of-combat deaths before capture are not counted: progress is not read
from the log yet).

## Helpers (`Fight`)

- Board: `Alive(side, tribe, except)`, `Neighbours(unit)`, `unit.Position`, `side.Minions` (left to right, dying
  minions included until the death phase), `side.IsFull`, `side.DeadThisFight`, `side.DeathrattlesTriggered`.
- Random (seeded per board; only `Random.Next(n)` is used so tests can script it): `RandomFriendly(self, tribe,
  includeSelf)`, `RandomEnemy(self, tribe)`, `RandomOf(list)`, `RandomDistinct(list, n)`, `Best(list, key)`
  (random tie-break).
- Summons: `SummonAt(place, minion)` – in a dead minion's old slot (deathrattles), or right of a living one;
  `SummonAt(place, minion, count)`; `Summon(side, minion, index)`; `Summon(side, cardId, atk, hp, keywords, slot)`.
  All return null once the side has 7 minions (extra summons are lost). Token stats: `Tokens.*`
  (`Tokens.Make(id, atk, hp, tribe, golden, keywords)` for others).
- Stats and keywords: `Buff(unit, atk, hp)`, `SetStats`, `GiveKeywords`, `RemoveKeywords`; for "your X have +a/+b
  (including ones summoned later this combat)" also call `side.AddSummonBuff(tribe, atk, hp)`.
- Damage: `DealDamage(source, target, amount)` (Divine Shield, Poisonous and Venomous apply; the target dies at the
  next death phase), `Destroy(target, source)`, `unit.Killer`.
- Attacks: `AttackNow(attacker, target)` (synchronous, out of turn: "summon X to attack the target first"),
  `AttackSoon(attacker)` ("attacks immediately": right after the current attack).
- Deathrattles: `TriggerDeathrattles(unit, times, withMultiplier)`; `DeathrattleTriggers(side)`;
  `unit.HasDeathrattle`, `unit.IsRally`, `unit.AddEffect(effect)` (gain another minion's deathrattle).
- Spells in combat: `CastSpell(side, spellId, target, resolve)`.
- Player context: `side.Info` (`HeroCardId`, `HeroPowerCardId`, `Extras`, `Tags`, `TavernTier`), `side.Hand` (local
  player only; always empty for the opponent), `side.ExtraDeathrattles`.
- Captured data on `unit.Spec`: `CardId` (normal id), `Golden`, `Tribes`, `BaseKeywords`, `RebornAttack`,
  `BaseHealth`, `ScriptData1/2`, `Enchantments`, `Remembered` (minions its start-of-combat trigger removed, e.g.
  Stitched Salvager's victim).

## Engine order (see `Fight` remarks)

attack declared → `OnAttack` / `OnFriendlyAttack` / `OnFriendlyAttacked` → death phase (attack cancelled if the
attacker or target died) → simultaneous combat damage + `OnAttackDamage` → damage hooks → `AfterAttack` /
`AfterFriendlyAttack` → death phase → queued "attacks immediately".

Death phase: remove every dead minion at once (attacking side first, each side left to right) → per dead minion:
deathrattles × (1 + extra triggers) then Reborn → "after a friendly/enemy minion dies" hooks per death → auras →
repeat while minions keep dying.

Attack order: each side's next attacker is the first minion with Attack at or right of the slot just right of its
last attacker. Minions summoned into the slot of a minion that was left of that point (e.g. the attacker's own
deathrattle tokens or reborn copy) wait for the next round; minions summoned right of it attack in turn.
**Unverified against real logs**; the rule matches the simulator's behaviour before effects existed.

## Testing an effect

Tests live in `tests/Hearthlens.Tests/Sim/`. `SimTestKit` builds boards and runs one deterministic fight:

```csharp
using static Hearthlens.Tests.Sim.SimTestKit;

[Fact]
public void Bonehead_summons_two_skeletons()
{
    // Their 5/50 attacks first and kills the Bonehead.
    var result = Fight(
        Side(Minion(1, 1, cardId: "BG28_300", tribes: Tribe.Undead)),
        Side(Minion(5, 50)),
        oursFirst: false,
        picks: [0]);                      // scripted Random.Next results, in call order (then 0)
    Assert.Equal(2, result.Log.Count(l => l.StartsWith("summoned our BG_ICC_026t", StringComparison.Ordinal)));
}
```

Golden minions: `Minion(2, 2, cardId: "BG28_300", golden: true)` (the normal card id plus `golden: true`). Buffed
minions: `Minion(printedAtk, printedHp, ...) with { Attack = 10, Health = 10, MaxHealth = 10 }` keeps the printed
values as base stats (what Reborn brings back).

- `Fight(...)` uses `EffectRegistry.Default` (all effects in Hearthlens.Core); pass
  `Effects(typeof(MyEffect))` to isolate one.
- `result.Ours` / `result.Theirs` are the survivors as `SimMinion`s; `result.Log` is a readable trace
  ("our X 1/1 attacks their Y 5/5", "… dies", "summoned …", "reborn …", "deathrattle of …").
- For odds, `CombatSimulator.Run(side, side, firstAttackerIsOurs, new SimOptions { Runs = …, Effects = … })`.
- `RelevanceTests.Built_in_effects_register_cleanly` fails on duplicate registrations.

## "Not simulated"

A captured minion is counted in "N minion effects not simulated" when its card is combat-relevant
(`CombatRelevance`) and `EffectRegistry.Implements(cardId, golden, isOpponent)` is false, or when an enchantment gave it
a deathrattle/Avenge/Rally and no registered effect covers any of its enchantments. Registering an effect therefore
removes the flag; `NeedsHand` effects keep it for the opponent.

`CombatRelevance.g.cs` is generated by `tools/effects/gen_combat_relevance.py` from the hand-labelled research data
(302 pool minions of patch 37.0.0.255130, tokens, and Timewarped minions classified by the text heuristic). After a
patch, regenerate it (see the script header). Cards it does not know fall back to `TextSuggestsCombatEffect` (English
card text) or to their mechanics (other locales), which over-report on purpose.
