"""Example helpers for BaudHoundTerm's agent: chess_read, chess_best and ocr_read in one small
HTTP server. Each takes the JSON the agent sends (plus the current screenshot, when the helper is
declared with "screenshot": true) and answers with JSON the agent reads.

    python screen_helpers.py [--port 8771] [--host 127.0.0.1]
    requires: python-chess, numpy, pillow; Stockfish for chess_best; Tesseract for ocr_read

agent.json, for example:
  "helpers": [
    {"name": "chess_read", "url": "http://127.0.0.1:8771/chess_read", "screenshot": true,
     "description": "The exact chess position on screen ...", "parameters": "{...}"}, ...]

Positions are on the agent's 0-1000 grid over the whole screen, like its own clicks. The board
reader is the one measured in testing: templates from the starting position, each square
classified by outline and brightness, moves joined with python-chess so legality corrects a
misread piece type (never occupancy or colour).
"""
import base64, io, json, os, re, shutil, subprocess, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import chess
import chess.engine
import numpy as np
from PIL import Image

STOCKFISH = os.environ.get("STOCKFISH", shutil.which("stockfish") or "stockfish")
TESSERACT = os.environ.get("TESSERACT", "tesseract").split()     # e.g. "ssh host tesseract"
CELL = 95
MARGIN = 4
EMPTY = 0.05
START = chess.Board().board_fen()

def cells(img):
    """64 crops, a8 first (row 0 is the top of the picture)."""
    for r in range(8):
        for c in range(8):
            yield r, c, img[r * CELL + MARGIN:(r + 1) * CELL - MARGIN, c * CELL + MARGIN:(c + 1) * CELL - MARGIN]


def piece_mask(cell):
    ring = np.concatenate([cell[:4].reshape(-1, 3), cell[-4:].reshape(-1, 3), cell[:, :4].reshape(-1, 3), cell[:, -4:].reshape(-1, 3)])
    bg = np.median(ring, axis=0)
    return np.abs(cell - bg).sum(axis=2) > 60


def features(cell, mask):
    """The piece cropped to its bounding box at 32x32: its outline, and its brightness with the
    background set to the piece's mean, so the eye and inner lines of a knight count, not just its outline."""
    ys, xs = np.nonzero(mask)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    box = mask[y0:y1, x0:x1]
    lum = (cell[y0:y1, x0:x1] * [0.299, 0.587, 0.114]).sum(axis=2)
    lum = np.where(box, lum, lum[box].mean())
    outline = np.asarray(Image.fromarray(box.astype(np.uint8) * 255).resize((32, 32))) > 127
    bright = np.asarray(Image.fromarray(lum.astype(np.float32)).resize((32, 32)), dtype=np.float32)
    bright = (bright - bright.mean()) / (bright.std() + 1e-6)
    return outline, bright


def similarity(a, b):
    """Half outline overlap (IoU), half brightness correlation."""
    iou = np.logical_and(a[0], b[0]).sum() / max(1, np.logical_or(a[0], b[0]).sum())
    ncc = float((a[1] * b[1]).mean())
    return 0.5 * iou + 0.5 * ncc


def describe(cell):
    """None for empty, else (is_white, features, mask fraction)."""
    m = piece_mask(cell)
    if m.mean() < EMPTY:
        return None
    lum = (cell[m] * [0.299, 0.587, 0.114]).sum(axis=1)
    return bool(np.median(lum) > 128), features(cell, m), float(m.mean())


def calibrate(img):
    """Templates from a starting position, or None when the picture is not one. Returns
    (templates {(is_white, piece_type): [shapes]}, white_at_bottom)."""
    occ = {(r, c): describe(cell) for r, c, cell in cells(img)}
    if any(occ[(r, c)] is None for r in (0, 1, 6, 7) for c in range(8)) or any(occ[(r, c)] for r in range(2, 6) for c in range(8)):
        return None
    bottom_white = np.mean([occ[(7, c)][0] for c in range(8)]) > 0.5
    if any(occ[(r, c)][0] != ((r >= 6) == bottom_white) for r in (0, 1, 6, 7) for c in range(8)):
        return None
    back = [chess.ROOK, chess.KNIGHT, chess.BISHOP, chess.QUEEN, chess.KING, chess.BISHOP, chess.KNIGHT, chess.ROOK]
    if not bottom_white:
        back = back[::-1]                # black at the bottom: files run h..a, so queen and king swap sides
    t = {}
    for r in (0, 1, 6, 7):
        for c in range(8):
            is_white, sh, _ = occ[(r, c)]
            t.setdefault((is_white, chess.PAWN if r in (1, 6) else back[c]), []).append(sh)
    return (t, bottom_white) if len(t) == 12 else None


def read(img, cal):
    """The board_fen the picture shows."""
    templates, bottom_white = cal
    board = chess.Board(None)
    for r, c, cell in cells(img):
        d = describe(cell)
        if d is None:
            continue
        is_white, sh, _ = d
        best = max((similarity(sh, s), kind) for (w, kind), shapes in templates.items() if w == is_white for s in shapes)
        rank, file = (7 - r, c) if bottom_white else (r, 7 - c)
        board.set_piece_at(chess.square(file, rank), chess.Piece(best[1], chess.WHITE if is_white else chess.BLACK))
    return board.board_fen()


def mismatch(placement, target):
    """(squares whose occupancy or colour differ, squares whose piece type alone differs). The
    reader is sure of the first, which is light against dark against empty, and less sure of the
    second: a black bishop on a highlighted square once read as a rook."""
    a, b = chess.BaseBoard(placement), chess.BaseBoard(target)
    hard = soft = 0
    for s in chess.SQUARES:
        p, q = a.piece_at(s), b.piece_at(s)
        if (p is None) != (q is None) or (p and q and p.color != q.color):
            hard += 1
        elif p and q and p.piece_type != q.piece_type:
            soft += 1
    return hard, soft


def explain(board, target):
    """The moves (one or two plies) that take board to the placement target. Legality corrects the
    reader: a result must match the reading exactly in occupancy and colour, may differ in at most
    one piece type, and must be the unique closest such result."""
    for depth in (1, 2):
        scored = []
        for m in board.legal_moves:
            board.push(m)
            if depth == 1:
                hard, soft = mismatch(board.board_fen(), target)
                if hard == 0 and soft <= 1:
                    scored.append((soft, [m]))
            else:
                for n in board.legal_moves:
                    board.push(n)
                    hard, soft = mismatch(board.board_fen(), target)
                    if hard == 0 and soft <= 1:
                        scored.append((soft, [m, n]))
                    board.pop()
            board.pop()
        if scored:
            scored.sort(key=lambda x: x[0])
            if len(scored) > 1 and scored[1][0] == scored[0][0]:
                return None              # two ways fit equally well: do not guess
            return scored[0][1]
    return None




def board_image(req):
    """The board cropped from the screenshot and scaled to 8 x 95 px, and the corners in pixels."""
    img = Image.open(io.BytesIO(base64.b64decode(req["screenshot"]))).convert("RGB")
    w, h = img.size
    (x0, y0), (x1, y1) = req["board"]["top_left"], req["board"]["bottom_right"]
    px = (x0 * (w - 1) / 1000, y0 * (h - 1) / 1000, x1 * (w - 1) / 1000, y1 * (h - 1) / 1000)
    crop = img.crop(tuple(round(v) for v in px)).resize((8 * CELL, 8 * CELL), Image.LANCZOS)
    return np.asarray(crop, dtype=np.int16), (x0, y0, x1, y1)


class Game:
    """One game as the board shows it, kept between calls."""
    cal = None
    board = chess.Board()
    sans = []
    squares = None           # the plain light and dark square colours, from the starting position
    unclear = None           # a reading no legal move explained, waiting for a second identical one


def square_colours(img):
    """The usual light and dark square colours: median border colour of the empty middle ranks."""
    light, dark = [], []
    for r, c, cell in cells(img):
        if 2 <= r <= 5:
            ring = np.concatenate([cell[:4].reshape(-1, 3), cell[-4:].reshape(-1, 3)])
            (light if (r + c) % 2 == 0 else dark).append(np.median(ring, axis=0))
    return np.median(light, axis=0), np.median(dark, axis=0)


def last_mover(img):
    """The colour that moved last, from chess.com's highlight of the last move's two squares: the
    piece standing on a highlighted square is the one that moved. None when it cannot be told."""
    light, dark = Game.squares
    movers = set()
    for r, c, cell in cells(img):
        ring = np.concatenate([cell[:4].reshape(-1, 3), cell[-4:].reshape(-1, 3)])
        usual = light if (r + c) % 2 == 0 else dark
        if np.abs(np.median(ring, axis=0) - usual).sum() > 40:
            d = describe(cell)
            if d is not None:
                movers.add(chess.WHITE if d[0] else chess.BLACK)
    return movers.pop() if len(movers) == 1 else None


def resync(seen, img):
    """A board built from the reading itself, when moves no longer explain it (the agent played
    something else, or several moves passed between calls). Castling rights are not known."""
    mover = last_mover(img)
    turn = (not mover) if mover is not None else Game.board.turn
    b = chess.Board(None)
    b.set_board_fen(seen)
    b.turn = turn
    return b if b.is_valid() else None


def grid_point(square, corners, white_bottom):
    x0, y0, x1, y1 = corners
    f, r = chess.square_file(square), chess.square_rank(square)
    col, row = (f, 7 - r) if white_bottom else (7 - f, r)
    return [round(x0 + (col + 0.5) * (x1 - x0) / 8), round(y0 + (row + 0.5) * (y1 - y0) / 8)]


def chess_read(req):
    img, _ = board_image(req)
    fresh = calibrate(img)
    if fresh:
        Game.cal, Game.board, Game.sans, Game.unclear = fresh, chess.Board(), [], None
        Game.squares = square_colours(img)
    if Game.cal is None:
        return {"status": "waiting for a new game: the starting position has not been seen yet, so the pieces "
                          "cannot be told apart. Start a new game, then call chess_read again."}
    seen = read(img, Game.cal)
    note = None
    if seen != Game.board.board_fen():
        moves = explain(Game.board, seen)
        if moves:
            for m in moves:
                Game.sans.append(Game.board.san(m))
                Game.board.push(m)
            Game.unclear = None
        elif Game.unclear == seen and (fresh_board := resync(seen, img)) is not None:
            # The same unexplained board twice: it is not a piece mid-move. Follow the board.
            Game.board, Game.unclear = fresh_board, None
            Game.sans.append("(resynced)")
            note = ("resynced: moves were played that the helper did not see one by one, so it now follows the board "
                    "as it is (castling rights unknown). Carry on from this position.")
        else:
            Game.unclear = seen
            note = ("the board shows something no legal move explains (a piece mid-move, a dialog over the board, or a "
                    "selected piece): call chess_read again in a moment")
    b = Game.board
    white_bottom = Game.cal[1]
    us = chess.WHITE if white_bottom else chess.BLACK
    status = "ok" if note is None else "resynced" if note.startswith("resynced") else "unclear"
    out = {"status": status, "fen": b.fen(),
           "you_play": "white" if us == chess.WHITE else "black",
           "to_move": "you" if b.turn == us else "the bot",
           "moves": " ".join(f"{i // 2 + 1}. {m}" if i % 2 == 0 else m for i, m in enumerate(Game.sans)),
           "last_move": Game.sans[-1] if Game.sans else None,
           "legal_moves": sorted(b.san(m) for m in b.legal_moves) if b.turn == us else [],
           "game_over": b.is_game_over(), "result": b.result() if b.is_game_over() else None}
    if note:
        out["note"] = note
    return out


def chess_best(req):
    if Game.cal is None:
        return {"error": "no game is being tracked: call chess_read first"}
    _, corners = board_image(req)
    b = Game.board
    if b.is_game_over():
        return {"error": f"the game is over ({b.result()})"}
    with chess.engine.SimpleEngine.popen_uci(STOCKFISH) as engine:
        engine.configure({"Skill Level": int(req.get("skill", 20))})
        move = engine.play(b, chess.engine.Limit(time=float(req.get("seconds", 0.5)))).move
    wb = Game.cal[1]
    out = {"move": b.san(move), "from": chess.square_name(move.from_square), "to": chess.square_name(move.to_square),
           "click_from": grid_point(move.from_square, corners, wb), "click_to": grid_point(move.to_square, corners, wb)}
    if move.promotion:
        out["promotion"] = "a promotion picker appears on the destination square: click the queen at click_to again"
    return out


def ocr_read(req):
    img = Image.open(io.BytesIO(base64.b64decode(req["screenshot"]))).convert("L")
    w, h = img.size
    x, y, rw, rh = (req[k] for k in ("x", "y", "width", "height"))
    box = (round(x * (w - 1) / 1000), round(y * (h - 1) / 1000), round((x + rw) * (w - 1) / 1000), round((y + rh) * (h - 1) / 1000))
    crop = img.crop(box)
    crop = crop.resize((crop.width * 3, crop.height * 3), Image.LANCZOS)
    b = io.BytesIO()
    crop.save(b, "PNG")
    text = subprocess.run(TESSERACT + ["stdin", "stdout", "--psm", "6"], input=b.getvalue(), capture_output=True, timeout=60).stdout.decode()
    return {"text": text.strip()}


ROUTES = {"/chess_read": chess_read, "/chess_best": chess_best, "/ocr_read": ocr_read}


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        fn = ROUTES.get(self.path)
        try:
            req = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
            out = fn(req) if fn else {"error": f"no helper at {self.path}"}
            code = 200 if fn else 404
        except Exception as e:  # the agent reads the message; the server keeps running
            out, code = {"error": f"{type(e).__name__}: {e}"}, 400
        data = json.dumps(out).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (self.path, fmt % args))


if __name__ == "__main__":
    port = int(sys.argv[sys.argv.index("--port") + 1]) if "--port" in sys.argv else 8771
    host = sys.argv[sys.argv.index("--host") + 1] if "--host" in sys.argv else "127.0.0.1"
    print(f"helpers on http://{host}:{port}: {', '.join(ROUTES)}", flush=True)
    ThreadingHTTPServer((host, port), Handler).serve_forever()
