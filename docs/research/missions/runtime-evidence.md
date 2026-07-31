# Mission runtime evidence

Evidence sources reviewed:

- DeepOpen `Mission`, `Objective`, `Dialogue` and `LevelScript` classes for older/J2ME observable behavior;
- GOF2HD `Mission`, `Generator`, `Level`, `LevelScript`, `GameRecord` and menu/status paths for Android native behavior;
- KaamoClubModApi only as evidence that PC custom missions require runtime hooks and an auxiliary representation, not as implementation source.

Independent corpus checks found no matching declarative mission container. The agreement between two engine reconstructions and the absent corpus file strongly supports a mixed runtime model: procedural freelance mission generation plus hard-coded campaign scripting.

License boundary: none of these sources is compiled, copied or mechanically translated into the MIT project.
