#!/usr/bin/env python3
"""Record real MyAnimeList API v2 responses for the series in JsonSamples/crunchyroll-history-2026-sample.json.

The client id comes from the environment only (never written anywhere):
    bella run -- python3 tests/FrunchyBackend.Tests/JsonSamples/mal/record.py      # Frunchy__Mal__ClientId
    MAL_CLIENT_ID=… python3 tests/FrunchyBackend.Tests/JsonSamples/mal/record.py

- Paced: one request every 1.2 s; 429/5xx/403 retried with backoff.
- For each series: search by its Crunchyroll title (the query Frunchy sends: long titles cut at the
  subtitle), then the details of the top results, their sequel/prequel chains and one level of
  side stories, capped per series. Bodies are saved exactly as received (the fields Frunchy asks for).
- manifest.json maps "search:<q>" and "details:<id>" to files, or to a status code.
"""
import hashlib, json, os, re, sys, time, urllib.parse, urllib.request, urllib.error

BASE = "https://api.myanimelist.net/v2/"
HERE = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(HERE, "../../../../JsonSamples/crunchyroll-history-2026-sample.json")
# Keep in sync with Infrastructure/MalApiModel.fs
SEARCH_FIELDS = "alternative_titles,media_type,start_date,start_season,mean,rating,nsfw,num_episodes,status"
DETAIL_FIELDS = "id,title,main_picture,alternative_titles,start_date,mean,media_type,status,genres,num_episodes,start_season,rating,nsfw,studios,related_anime"
PACE = 1.2
CAP_PER_SERIES = 12
FOLLOW_CHAIN = {"sequel", "prequel"}
FOLLOW_ONCE = {"side_story", "parent_story", "summary", "other", "alternative_version", "full_story", "spin_off"}

CLIENT_ID = os.environ.get("Frunchy__Mal__ClientId") or os.environ.get("MAL_CLIENT_ID")
if not CLIENT_ID:
    sys.exit("Set Frunchy__Mal__ClientId (e.g. run through `bella run --`) or MAL_CLIENT_ID")

last = [0.0]

def get(path):
    for attempt in range(5):
        wait = PACE - (time.time() - last[0])
        if wait > 0:
            time.sleep(wait)
        last[0] = time.time()
        req = urllib.request.Request(BASE + path, headers={"X-MAL-CLIENT-ID": CLIENT_ID, "User-Agent": "Frunchy-Backend/fixture-recorder"})
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return 200, r.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", "replace")
            if e.code in (403, 429, 500, 502, 503, 504) and attempt < 4:
                time.sleep(3 * (attempt + 1))
                continue
            return e.code, body
        except Exception as e:
            if attempt < 4:
                time.sleep(3 * (attempt + 1))
                continue
            return 599, json.dumps({"error": "recorder_error", "message": str(e)})

def query_for(title):
    # Same rule as MalClient.Provider.Query
    title = title.strip()
    if len(title) <= 64:
        return title
    cut = re.split(r":| - |–|—| -", title)[0].strip()
    if 3 <= len(cut) <= 64:
        return cut
    words = title[:64]
    space = words.rfind(" ")
    return (words[:space] if space > 3 else words).strip()

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
    manifest = {k: v for k, v in manifest.items() if "file" in v or v.get("status") == 404}

    def save(key, fname, status, body):
        if status == 200:
            with open(os.path.join(HERE, fname), "w") as f:
                f.write(body)
            manifest[key] = {"file": fname}
        else:
            manifest[key] = {"status": status, "body": body[:2000]}
        with open(manifest_path, "w") as f:
            json.dump(manifest, f, indent=1, sort_keys=True)

    def details(mal_id):
        key = f"details:{mal_id}"
        if key in manifest:
            entry = manifest[key]
            return json.load(open(os.path.join(HERE, entry["file"]))) if "file" in entry else None
        status, body = get(f"anime/{mal_id}?fields={DETAIL_FIELDS}")
        save(key, f"details-{mal_id}.json" if status == 200 else None, status, body)
        if status != 200:
            print(f"  details {mal_id}: {status}", file=sys.stderr)
            return None
        return json.loads(body)

    only = sys.argv[1:]
    for title in series_titles():
        if only and title not in only:
            continue
        q = query_for(title)
        key = "search:" + q
        if key not in manifest:
            status, body = get("anime?" + urllib.parse.urlencode({"q": q, "limit": 10, "fields": SEARCH_FIELDS}))
            name = "search-" + hashlib.sha1(q.encode()).hexdigest()[:10] + ".json"
            save(key, name if status == 200 else None, status, body)
            if status != 200:
                print(f"search {q!r}: {status} {body[:200]}", file=sys.stderr)
                continue
        entry = manifest[key]
        if "file" not in entry:
            continue
        results = [d["node"] for d in json.load(open(os.path.join(HERE, entry["file"])))["data"]]
        print(f"{title}: {len(results)} results", file=sys.stderr)
        fetched, queue, seen, once = 0, [r["id"] for r in results[:3]], set(), set()
        while queue and fetched < CAP_PER_SERIES:
            mid = queue.pop(0)
            if mid in seen:
                continue
            seen.add(mid)
            had = f"details:{mid}" in manifest
            a = details(mid)
            if not had:
                fetched += 1
            if not a:
                continue
            for rel in a.get("related_anime") or []:
                rid, kind = rel["node"]["id"], rel.get("relation_type")
                if kind in FOLLOW_CHAIN:
                    queue.append(rid)
                elif kind in FOLLOW_ONCE and mid not in once:
                    queue.append(rid)
            once.add(mid)

    if "details:0" not in manifest:
        status, body = get(f"anime/0?fields={DETAIL_FIELDS}")
        save("details:0", None, status, body)
    print("done", file=sys.stderr)

if __name__ == "__main__":
    main()
