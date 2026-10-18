---
name: chess.com
description: Open chess.com in a browser and play a game against a bot
protocols: vnc
steps: 300
helpers: chess_read, chess_best
---
A whole game is long; this skill raises the step limit to 300.

With the chess_read and chess_best helpers (they appear among your tools when set up), let them
do the chess and do the browser yourself:
- Start a new game (below), then find the board's two corners once, by zooming: the outer edge
  of the 8x8 squares, top-left and bottom-right. Pass them as "board" to every helper call, e.g.
  {"board": {"top_left": [218, 190], "bottom_right": [614, 894]}}.
- Call chess_read right after the game starts: it learns the pieces from the starting position.
- Your turn ("to_move": "you"): call chess_best, click click_from (target: the piece's name),
  then click_to (target: "empty square", or the piece taken). Then call chess_read: its "moves"
  must now end with your move. If not, call chess_read once more before trying again.
- The bot's turn: wait 3 s, then chess_read, until "to_move" is "you" again.
- Never choose a move yourself, and believe chess_read over what you think you see.
- A "note" or "unclear" status: close any dialog over the board, wait, and call chess_read again.
- When "game_over" is true, report "result" and "moves".
Without the helpers, play with the rules below.

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
- "New Game" only takes you back to the bot list: press "Play" after it. A game has started only
  when the right panel shows the move list (empty at first) and the big green Play button is gone.
  Pieces will not move before that; do not try.

Finding the board, once per game:
- Zoom on the board's top-left corner, then its bottom-right corner (the board's own edge, not
  the name labels above and below it). Note which colour is at the bottom (white when you play
  white: a1 is bottom-left).
- Work out one table and keep it in every `seen` for the rest of the game: the x of each file
  column and the y of each rank row. Width and height of a square are NOT the same on the grid:
  the grid is 0-1000 both ways on a wide screen, so a square is about 1.8 times taller than wide.
  - column width = (right - left) / 8; file x = left + width * (0.5, 1.5, ... 7.5)
  - row height = (bottom - top) / 8; rank y = top + height * (0.5, 1.5, ... 7.5) from the top row
- Write the table with every letter and number next to its value, so a square is a lookup, never
  a count. Worked example, white at the bottom, board 218,190 to 614,894 (width 49.5, height 88):
  `x: a243 b292 c342 d391 e441 f490 g540 h589; y: 8=234 7=322 6=410 5=498 4=586 3=674 2=762 1=850`
  so e2 is (441, 762), g1 is (540, 850) and h1 is (589, 850). Mixing up g and h was a real
  mistake: 589 is h, the rook's file. With black at the bottom, a is on the right and 1 at the top.
- If your corners are within a few points of the example's (218,190 and 614,894), the board is
  the same: copy the example table exactly, do not work it out again.
- Take every square from the table. Do not re-find squares by eye or redo the sums per move.

Choosing a move:
- Before you click, check the move is legal from the position you rebuilt from the move list:
  the piece is on that square, the path is clear, and bishops stay on their colour (a c1 bishop
  can never reach d3). Pawns move straight ahead only onto an empty square, and capture only
  diagonally. An illegal move does nothing and costs steps.
- Let the board tell you: after you click a piece, chess.com marks every square it may go to with
  a grey dot (a ring on a piece it may take). Zoom once on the board and click only a marked
  square. No marks at all: you picked the wrong square, or that piece cannot move.
- Keep `failed: f2-f4, d3xd4` in `seen` for this turn: moves that did not register. Never try
  one of them again; pick another move.

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

After every move, the move list decides:
- Right after clicking the destination, zoom on the move list in the right panel and read its
  last entry. Do not wait first, and do not judge from the board.
  - Your move is not in the list: it did not happen (wrong square, or not legal). Nothing is
    coming from the bot. Rebuild the position, check the squares in your table, and move again.
  - Your move is the last entry: the bot is to move. `wait` up to 5 s, then read the list again.
  - The bot's reply is after your move: it is your turn. Read it and choose your next move.
- Read the bot's move twice before you believe it: zoom on the last row of the move list with
  size 200, then again with size 120, and accept it only when both readings agree. If they differ,
  zoom a third time and take the reading that appears twice. The bot's moves are drawn in a thin
  font, and one misread letter (Bb6 for Bd6) makes every later move wrong; two enlarged reads that
  agree were right 144 times out of 144 in testing.
- The bot answers within seconds. Never `wait` twice in a row without reading the move list.
- Tips and pop-ups beside the move list do not matter; leave them unless they cover the board.
- The move list is the truth. Keep the whole game in `seen` in algebraic notation, e.g.
  `moves: 1. e4 e5 2. Nf3 Nc6 3. Bb5 a6`. Pictures are dropped; this line is your only memory.
- Copy the list exactly as the screen shows it, up to its last entry. Never add a move you have
  not read there: the last number on screen is the last move made.
- Before each move, check its number against the list: if the list already shows the move you
  are about to make, it is done, and it may be your turn again. Clicking a piece over and over
  means your idea of the position is out of date: read the list.
- Before choosing a move, rebuild the position from that list, not from an old picture. Check that
  your piece is really on the square you mean (zoom if unsure).

Playing:
- Develop pieces, castle early, keep the king safe, do not leave pieces where they can be taken
  for free. Before each move ask: what does the bot's last move attack?
- When the game ends (checkmate, resignation, draw), read the result from the dialog and say it,
  with the moves, in your final answer. Do not start another game unless the goal says so.
