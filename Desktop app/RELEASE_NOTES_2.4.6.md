# CANalyser 2.4.6

- Online analyses are no longer limited to 200 selected files. Download-plan requests are batched while the selected files remain one analysis.
- Large MF4 archives are converted in bounded groups so Windows command-line limits do not block a full-day import.
- Import safety limits remain explicit: maximum 10,000 files, 512 MB per file, and 4 GB total unpacked data.
