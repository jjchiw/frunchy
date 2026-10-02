#!/usr/bin/env python3
"""Record real Jikan v4 responses for the series in JsonSamples/crunchyroll-history-2026-sample.json.

Run from the repo root:  python3 tests/FrunchyBackend.Tests/JsonSamples/jikan/record.py

- Paced: one request every 1.2 s (Jikan allows 3/s and 60/min); 429/5xx retried with backoff.
- For each series: search by its Crunchyroll title (the exact query Frunchy sends), then the /full
  of the top search results, their Sequel/Prequel chains and one level of side stories, capped
  per series.
- Bodies are trimmed to the fields Frunchy reads (no synopsis, trailers, streaming lists...) so the
  fixtures stay small. They are otherwise exactly what Jikan returned.
- manifest.json maps "search:<q>" and "full:<id>" to files (or to a status code with its body).
"""
import hashlib, json, os, sys, time, urllib.parse, urllib.request, urllib.error

BASE = "https://api.jikan.moe/v4/"
HERE = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(HERE, "../../../../JsonSamples/crunchyroll-history-2026-sample.json")
PACE = 1.2
CAP_PER_SERIES = 12
KEEP = ["mal_id", "url", "titles", "title", "title_english", "title_japanese", "title_synonyms", "type",
        "episodes", "status", "airing", "score", "season", "year", "rating", "genres", "explicit_genres",
        "themes", "demographics", "studios"]
FOLLOW_CHAIN = {"Sequel", "Prequel"}
FOLLOW_ONCE = {"Side Story", "Parent Story", "Summary", "Other", "Alternative Version", "Full Story"}

last = [0.0]

def get(path):
    for attempt in range(5):
        wait = PACE - (time.time() - last[0])
        if wait > 0:
            time.sleep(wait)
        last[0] = time.time()
        req = urllib.request.Request(BASE + path, headers={"User-Agent": "Frunchy-Backend/fixture-recorder"})
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return 200, r.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", "replace")
            if e.code in (429, 500, 502, 503, 504) and attempt < 4:
                time.sleep(3 * (attempt + 1))
                continue
            return e.code, body
        except Exception as e:  # timeouts, resets
            if attempt < 4:
                time.sleep(3 * (attempt + 1))
                continue
            return 599, json.dumps({"status": 599, "type": "RecorderError", "message": str(e)})

def trim_anime(a, with_relations):
    t = {k: a.get(k) for k in KEEP}
    imgs = a.get("images") or {}
    t["images"] = {k: {"image_url": (imgs.get(k) or {}).get("image_url")} for k in ("jpg", "webp")}
    aired = a.get("aired") or {}
    t["aired"] = {"from": aired.get("from"), "to": aired.get("to")}
    if with_relations:
        t["relations"] = a.get("relations") or []
    return t

def series_titles():
    d = json.load(open(SAMPLE))
    titles = []
    for p in d["pages"]:
        body = p["body"] if isinstance(p["body"], dict) else json.loads(p["body"])
        for e in body["data"]:
            m = (e.get("panel") or {}).get("episode_metadata") or {}
            t = m.get("series_title")
            if t and t not in titles:
                titles.append(t)
    return titles

def main():
    manifest_path = os.path.join(HERE, "manifest.json")
    manifest = json.load(open(manifest_path)) if os.path.exists(manifest_path) else {}
    # Temporary failures (5xx, 429, recorder errors) are retried on the next run
    manifest = {k: v for k, v in manifest.items() if "file" in v or v.get("status", 0) < 429 or v.get("status") == 404}

    def save(key, fname, status, body):
        if status == 200:
            with open(os.path.join(HERE, fname), "w") as f:
                f.write(body)
            manifest[key] = {"file": fname}
        else:
            manifest[key] = {"status": status, "body": body[:2000]}
        with open(manifest_path, "w") as f:
            json.dump(manifest, f, indent=1, sort_keys=True)

    def full(mal_id):
        key = f"full:{mal_id}"
        if key in manifest:
            entry = manifest[key]
            if "file" in entry:
                return json.load(open(os.path.join(HERE, entry["file"])))["data"]
            return None
        status, body = get(f"anime/{mal_id}/full")
        if status == 200:
            a = json.loads(body)["data"]
            save(key, f"full-{mal_id}.json", 200, json.dumps({"data": trim_anime(a, True)}, ensure_ascii=False))
            return a
        save(key, None, status, body)
        print(f"  full {mal_id}: {status}", file=sys.stderr)
        return None

    only = sys.argv[1:]
    for title in series_titles():
        if only and title not in only:
            continue
        q = title
        key = "search:" + q
        if key not in manifest:
            status, body = get("anime?" + urllib.parse.urlencode({"q": q, "limit": 10}) + "&sfw")
            if status == 200:
                d = json.loads(body)
                trimmed = {"data": [trim_anime(a, False) for a in d.get("data", [])], "pagination": d.get("pagination")}
                save(key, "search-" + hashlib.sha1(q.encode()).hexdigest()[:10] + ".json", 200, json.dumps(trimmed, ensure_ascii=False))
            else:
                save(key, None, status, body)
                print(f"search {q!r}: {status}", file=sys.stderr)
                continue
        entry = manifest[key]
        if "file" not in entry:
            continue
        results = json.load(open(os.path.join(HERE, entry["file"])))["data"]
        print(f"{title}: {len(results)} results", file=sys.stderr)
        fetched, queue, seen = 0, [r["mal_id"] for r in results[:3]], set()
        depth_once = set()
        while queue and fetched < CAP_PER_SERIES:
            mid = queue.pop(0)
            if mid in seen:
                continue
            seen.add(mid)
            had = f"full:{mid}" in manifest
            a = full(mid)
            if not had:
                fetched += 1
            if not a:
                continue
            for rel in a.get("relations") or []:
                for e in rel.get("entry") or []:
                    if e.get("type") != "anime":
                        continue
                    if rel["relation"] in FOLLOW_CHAIN:
                        queue.append(e["mal_id"])
                    elif rel["relation"] in FOLLOW_ONCE and mid not in depth_once:
                        queue.append(e["mal_id"])
            depth_once.add(mid)

    # A by-id 404 with its error body, and a search with no results
    if "full:0" not in manifest:
        status, body = get("anime/0/full")
        save("full:0", None, status, body)
    qn = "zzqx frunchy no such anime"
    if "search:" + qn not in manifest:
        status, body = get("anime?" + urllib.parse.urlencode({"q": qn, "limit": 10}) + "&sfw")
        save("search:" + qn, "search-no-results.json" if status == 200 else None, status, body)
    print("done", file=sys.stderr)

if __name__ == "__main__":
    main()
