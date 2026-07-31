# Mission rewards

Mission runtime objects contain reward, costs, bonus, difficulty and production-good/item values. Procedural generation calculates these from mission type, random selection, campaign progress and referenced entities.

No standalone reward record was found. A future read-only save inspector may display persisted values, but a mission authoring UI must not imply that changing a database row can replace the generator logic.
