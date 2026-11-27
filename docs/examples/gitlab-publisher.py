"""Optional SourceGit publisher using a locally authenticated GitLab CLI."""

import argparse
import json
import subprocess
import sys
from urllib.parse import quote, urlencode, urlsplit


def project(url):
    if "://" in url:
        parsed = urlsplit(url)
        host = parsed.netloc if parsed.scheme in ("https", "http") else parsed.hostname
        path = parsed.path
    else:
        host, path = url.partition(":")[::2]
        host = host.rsplit("@", 1)[-1]
    path = path.strip("/").removesuffix(".git")
    if not host or "/" not in path:
        raise ValueError("Expected a GitLab remote URL with a group and project.")
    return host, path


def api(host, method, path, payload=None):
    args = ["glab", "api", "--hostname", host, "--method", method, path]
    body = None
    if payload is not None:
        args += ["--input", "-"]
        body = json.dumps(payload)
    result = subprocess.run(args, input=body, capture_output=True, encoding="utf-8", check=False)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "glab could not complete the request.")
    return json.loads(result.stdout)


def publish(request, host_override):
    source, target = request["source"], request["target"]
    source_host, source_path = project(source["url"])
    target_host, target_path = project(target["url"])
    if source_host.lower() != target_host.lower() and host_override is None:
        raise ValueError("Use a common GitLab host. Set --host explicitly for different aliases of that instance.")
    host = host_override or target_host
    source_id = api(host, "GET", "projects/" + quote(source_path, safe=""))["id"]
    target_id = source_id if source_path == target_path else api(host, "GET", "projects/" + quote(target_path, safe=""))["id"]
    if source_id == target_id and source["branch"] == target["branch"]:
        raise ValueError("Choose different source and target branches.")

    query = urlencode({
        "state": "opened", "scope": "all", "source_branch": source["branch"],
        "target_branch": target["branch"], "per_page": 100,
    })
    page = 1
    while True:
        items = api(host, "GET", f"projects/{target_id}/merge_requests?{query}&page={page}")
        for item in items:
            if item["source_project_id"] == source_id and item["target_project_id"] == target_id:
                return {"status": "existing", "url": item["web_url"]}
        if len(items) < 100:
            break
        page += 1

    if request["operation"] == "find":
        return {"status": "missing"}
    if request["operation"] != "create":
        raise ValueError("Unknown publication operation.")

    created = api(host, "POST", f"projects/{source_id}/merge_requests", {
        "source_branch": source["branch"],
        "target_branch": target["branch"],
        "target_project_id": target_id,
        "title": request["title"],
        "description": request["description"],
    })
    return {"status": "created", "url": created["web_url"]}


def main():
    options = argparse.ArgumentParser(description=__doc__)
    options.add_argument("--host", help="Override the GitLab hostname, for SSH aliases or self-managed servers.")
    args = options.parse_args()
    try:
        request = json.loads(sys.stdin.buffer.read())
        print(json.dumps(publish(request, args.host)))
    except (OSError, ValueError, KeyError, RuntimeError, TypeError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
