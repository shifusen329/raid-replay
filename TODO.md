# TODO

## Maybe / low priority

- **Keep each pull's report (report history).** Today a pull's report is rebuilt every time it's opened, with the current rules and learned positions, and only the last live report is kept, in memory. So the Report tab for an old pull can differ from the after-action report shown that night. The difference is usually marginal.
  - Save each live report when it's made, with the plugin version that made it. Show it as "reported then", next to a fresh re-analysis.
  - Storage: SQLite, or one compressed results file per log like the index cache. About 10–30 MB for ~8,700 pulls. SQLite needs `e_sqlite3.dll` next to the plugin's dll.
  - Would also enable stats across pulls and filters in the pull list:
    - who fails which mechanic;
    - mitigation compliance;
    - progress per lockout;
    - e.g. "pulls that wiped to Forsaken towers".

    The backlog would be analyzed in the background, newest first. That's about 30 minutes for the DMU pulls alone.
  - To rebuild an old report exactly, learn positions only from the pulls before it (an "as of" option for `rr analyze` and the Report tab).
