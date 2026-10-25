"""Writes src/Essence.Ranking/QuebecBoundary.txt: Québec's boundary from OpenStreetMap, simplified, for the API's test of
whether a trip's destination is in Québec (QuebecArea.Contains).

    curl -fsSL -o quebec.poly "https://polygons.openstreetmap.fr/get_poly.py?id=61549&params=0"
    python3 tools/quebec-boundary/make-boundary.py quebec.poly src/Essence.Ranking/QuebecBoundary.txt

61549 is the province's boundary relation. Each ring is simplified (Douglas-Peucker) to within 20 m south of 49.5° N,
where the borders with Ontario, the United States and New Brunswick run through towns, and 300 m north of it. Each ring
is written as one line, Google's encoded polyline at precision 5; a hole (none today) starts with "!".
"""
import math
import sys

SOUTH_TOLERANCE_KM = 0.02
NORTH_TOLERANCE_KM = 0.3
NORTH_FROM = 49.5
KM_PER_DEGREE = 6371.0088 * math.pi / 180


def read_poly(path):
    """The rings of an Osmosis .poly file, as (name, [(lat, lon), ...])."""
    rings, points, name = [], None, None
    with open(path, encoding="utf-8") as lines:
        next(lines)  # the polygon's name
        for line in lines:
            text = line.strip()
            if not text:
                continue
            if line[0] not in " \t":
                if text == "END":
                    if points is not None:
                        rings.append((name, points))
                        points = None
                else:
                    name, points = text, []
                continue
            lon, lat = map(float, text.split())
            points.append((lat, lon))
    return rings


def segment_km(p, a, b):
    """From p to the segment a-b, on a plane tangent at a."""
    scale = math.cos(math.radians(a[0])) * KM_PER_DEGREE
    bx, by = (b[1] - a[1]) * scale, (b[0] - a[0]) * KM_PER_DEGREE
    px, py = (p[1] - a[1]) * scale, (p[0] - a[0]) * KM_PER_DEGREE
    length = bx * bx + by * by
    t = 0 if length == 0 else max(0, min(1, (px * bx + py * by) / length))
    return math.hypot(px - t * bx, py - t * by)


def simplify(points):
    """Douglas-Peucker, the tolerance taken at each point's latitude."""
    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    spans = [(0, len(points) - 1)]
    while spans:
        first, last = spans.pop()
        farthest, worst = -1, 1.0
        for i in range(first + 1, last):
            tolerance = SOUTH_TOLERANCE_KM if points[i][0] < NORTH_FROM else NORTH_TOLERANCE_KM
            ratio = segment_km(points[i], points[first], points[last]) / tolerance
            if ratio > worst:
                farthest, worst = i, ratio
        if farthest >= 0:
            keep[farthest] = True
            spans += [(first, farthest), (farthest, last)]
    return [p for p, kept in zip(points, keep) if kept]


def encode(points, precision=5):
    factor = 10 ** precision
    out, previous = [], (0, 0)

    def value(v):
        v = ~(v << 1) if v < 0 else v << 1
        chars = ""
        while v >= 0x20:
            chars += chr((0x20 | (v & 0x1F)) + 63)
            v >>= 5
        return chars + chr(v + 63)

    for lat, lon in points:
        current = (round(lat * factor), round(lon * factor))
        out.append(value(current[0] - previous[0]) + value(current[1] - previous[1]))
        previous = current
    return "".join(out)


def main(source, target):
    lines = ["# Québec's boundary: OpenStreetMap relation 61549, © OpenStreetMap contributors (ODbL 1.0).",
             "# Simplified to 20 m south of 49.5° N and 300 m north of it by tools/quebec-boundary/make-boundary.py.",
             "# One ring per line, an encoded polyline (precision 5); a hole starts with \"!\"."]
    total = 0
    for name, points in read_poly(source):
        if points[0] == points[-1]:
            points = points[:-1]
        # Two halves, so that each has fixed ends.
        middle = len(points) // 2
        ring = simplify(points[:middle + 1])[:-1] + simplify(points[middle:] + [points[0]])[:-1]
        total += len(ring)
        lines.append(("!" if name.startswith("!") else "") + encode(ring))
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines) + "\n")
    print(f"{len(lines) - 3} rings, {total} points", file=sys.stderr)


if __name__ == "__main__":
    main(*sys.argv[1:3])
