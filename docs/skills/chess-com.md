---
name: chess.com
description: Open chess.com in a browser and play a game against a bot
protocols: vnc
steps: 300
---
A whole game is long; this skill raises the step limit to 300.

Getting there, keyboard first:
- Open the browser with `key win`, `type firefox` (or edge, chrome), `key enter`. Never click
  taskbar icons: they are small and the click check misreads them.
- Go to a page with `key ctrl+l`, then `type https://www.chess.com/play/computer` with enter.
- Close cookie banners, sign-up offers and tips with their Close, X, "No thanks" or `key esc`
  before anything else. They come back after games too.

Starting a game:
- If the page already shows a game, finished or not, start a new one ("New Game", or the bot list
  and "Play") unless the goal says to continue it. An old position is not yours to rescue.
- The right-hand panel lists bots. Click a bot, then the green "Play" button at the bottom of that
  panel. If a second screen asks for colour or mode, choose and press "Play" again.
- If a click on "Play" is refused by the check, zoom on the bottom of the right panel once, then
  click the middle of the green button; it is wide, so aim at its centre, not its label.

Finding the board, once per game:
- Zoom on the board's top-left corner, then its bottom-right corner (the board's own edge, not
  the name labels above and below it). Note which colour is at the bottom (white when you play
  white: a1 is bottom-left).
- Work out one table and keep it in every `seen` for the rest of the game: the x of each file
  column and the y of each rank row. Width and height of a square are NOT the same on the grid:
  the grid is 0-1000 both ways on a wide screen, so a square is about 1.8 times taller than wide.
  - column width = (right - left) / 8; file x = left + width * (0.5, 1.5, ... 7.5)
  - row height = (bottom - top) / 8; rank y = top + height * (0.5, 1.5, ... 7.5) from the top row
- Worked example, white at the bottom, board 218,190 to 614,894: width 49.5, height 88.
  `files a-h x: 243 292 342 391 441 490 540 589; ranks 8-1 y: 234 322 410 498 586 674 762 850`
  so e2 is (441, 762) and c1 is (342, 850). With black at the bottom, files run h-a and ranks 1-8.
- Take every square from the table. Do not re-find squares by eye or redo the sums per move.

Making a move:
- Click the piece's square, then the destination square. Name only what a close-up of that one
  square shows: "white knight" for the piece, then "empty square" (or "black pawn" for a
  capture). Leave the square's name ("g1", "f3") out of the target: it is not printed on the
  square, so the check cannot see it. Keep which square you mean in your own notes.
- Never copy what a refused check said into your next target. If the check names a different
  piece than you expected, your idea of the position may be wrong: read the move list again.
- A move that is not legal snaps back; nothing happens. Read the position again.
- If a click on a square is refused, the table is off: zoom on that square once, correct the
  table, and carry on. After two misses on one piece, play a different move instead.
- Promotion shows a column of pieces on the destination file: click the queen unless you need
  another piece.

After every move:
- Use `wait` for the bot's reply, then zoom on the move list in the right panel and read it.
- The move list is the truth. Keep the whole game in `seen` in algebraic notation, e.g.
  `moves: 1. e4 e5 2. Nf3 Nc6 3. Bb5 a6`. Pictures are dropped; this line is your only memory.
- Before choosing a move, rebuild the position from that list, not from an old picture. Check that
  your piece is really on the square you mean (zoom if unsure).

Playing:
- Develop pieces, castle early, keep the king safe, do not leave pieces where they can be taken
  for free. Before each move ask: what does the bot's last move attack?
- When the game ends (checkmate, resignation, draw), read the result from the dialog and say it,
  with the moves, in your final answer. Do not start another game unless the goal says so.
