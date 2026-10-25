"""Writes tests/Hearthpulse.Tests/Fixtures/Secrets/secrets.enUS.json: every collectible Secret from HearthstoneJSON (enUS).

SecretRulesTests checks that each one has an entry in Core/Secrets/SecretRules.cs, so after a patch:

    python tools/secrets/secrets_fixture.py [cards.json]

(without an argument it downloads https://api.hearthstonejson.com/v1/latest/enUS/cards.json), then add the new
Secrets to the rule table from their card text.
"""
import json
import pathlib
import sys
import urllib.request

URL = "https://api.hearthstonejson.com/v1/latest/enUS/cards.json"
OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "Hearthpulse.Tests" / "Fixtures" / "Secrets" / "secrets.enUS.json"
FIELDS = ("id", "dbfId", "name", "text", "cost", "type", "cardClass", "set", "rarity", "mechanics", "collectible")


def main() -> None:
    if len(sys.argv) > 1:
        cards = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
    else:
        with urllib.request.urlopen(URL, timeout=60) as response:
            cards = json.loads(response.read().decode("utf-8"))
    secrets = [
        {k: c[k] for k in FIELDS if k in c}
        for c in cards
        if c.get("collectible") and c.get("type") == "SPELL" and "SECRET" in c.get("mechanics", [])
    ]
    secrets.sort(key=lambda c: (c.get("cardClass", ""), c["id"]))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(secrets, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"{len(secrets)} Secrets -> {OUT}")


if __name__ == "__main__":
    main()
