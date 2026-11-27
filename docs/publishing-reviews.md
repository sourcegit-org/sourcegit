# Publishing pull and merge requests

## Set up PR/MR credentials

In **Preferences → PR/MR credentials**, add a GitHub or GitLab connection. Enter the HTTPS address of the instance, such as `https://github.com`, `https://gitlab.com`, or your self-hosted server, and paste the token. For GitHub, use a token with access to the repositories and permission to write pull requests. For GitLab, use a token with the `api` scope and access to both projects. SourceGit stores tokens in the operating system's credential store and selects a connection by the target remote's host. An SSH remote can use the same PR/MR connection when its hostname matches the configured instance. Git reads and pushes use the repository's existing Git authentication.

## Choose a destination and publish

1. In **Repository configuration → Publish Pull/Merge Request**, optionally set the source remote, target remote, default target branch and description template. For each target remote, SourceGit prefers the configured branch, then the last successful selection, then the remote's default branch.
2. Right-click a local branch and choose **AI → Create PR/MR…**. Check the source remote, target remote, target branch and matched connection. For a fork, select its remote as the source and the upstream repository's remote as the target. Both projects must be on the configured GitHub or GitLab instance.
3. Enter a title and description, or enable **Generate PR/MR draft** in an external AI profile and generate an editable draft. Drafting uses local Git objects for both selected revisions. Fetch the target branch and refresh the dialog if the revision shown in the dialog is missing locally.
4. Select **Push if needed and publish** after checking the refs and text. SourceGit verifies the reviewed tips and pushes the source branch if it needs updating. A rejected push stops publication. After a failed or interrupted create request, try again from the dialog. SourceGit checks for an existing open request for the same source and target before creating one.

### Troubleshooting

- If a connection is missing, use **Set access token** in the dialog. Match the target remote's host. GitHub and GitLab credentials control the provider API request.
- If Git rejects the push or cannot read either remote, check the remote URL and the repository's Git authentication, then refresh the dialog.
- If the provider denies publication, verify the token's access to both projects and its permission to create a pull or merge request.

## Legacy publisher protocol

An existing command-based publisher can still run as a fallback when the selected target has no matching connection. The command receives a UTF-8 JSON request on stdin. Both `find` and `create` use the same publication metadata.

```json
{
  "operation": "find",
  "source": {
    "remote": "fork",
    "url": "https://forge.example.com/user/project.git",
    "branch": "fix/example",
    "commit": "the reviewed source commit ID"
  },
  "target": {
    "remote": "upstream",
    "url": "https://forge.example.com/team/project.git",
    "branch": "main",
    "commit": "the reviewed target commit ID"
  },
  "title": "Fix the example",
  "description": "Markdown description"
}
```

- `find` is a read-only lookup. Return `{"status":"missing"}` or `{"status":"existing","url":"https://…"}` for an open request matching both projects and branches.
- `create` returns `{"status":"created","url":"https://…"}` or the same `existing` response if a matching request was created in the meantime.
- Write exactly one JSON result to stdout. Report failures on stderr with a nonzero exit code. Leave Git pushes to SourceGit.

The [GitLab command adapter](examples/gitlab-publisher.py) remains available for existing publisher configurations. It uses Python 3.9+ and an authenticated [GitLab CLI](https://docs.gitlab.com/cli/).
