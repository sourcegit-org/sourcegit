# External AI commands

Open **Preferences → AI → External CLI profiles** and add a profile. Set its executable and enter one argument per line. Put spaces inside the argument that needs them, without surrounding quotes. Select the tasks for this profile. A command can be a program from `PATH` or an absolute executable path. For a Windows script, select its interpreter as the executable and put the script path in the arguments.

SourceGit launches the command in the selected worktree. The command inherits your user permissions and can send the supplied prompt to its configured provider. Configure a headless, read-only command for analysis and generation. Set model and reasoning options in the command arguments, or create separate profiles for fast generation and deeper analysis.

## Input and output

- By default the command reads the UTF-8 prompt on stdin and writes the answer to stdout with exit code zero. The CLI should send diagnostics to stderr.
- To use a prompt file, select the prompt-file option and insert `{promptFile}` in one argument. The file is UTF-8 and temporary. A CLI that accepts an `@file` argument can use `@{promptFile}`.
- Select **Read SourceGit JSONL output** for a command or adapter that emits one UTF-8 JSON object per stdout line:

  | Type | Fields | Meaning |
  | --- | --- | --- |
  | `answer` | `text` | One final answer |
  | `thinking` | `text` | Optional reasoning output exposed by the provider |
  | `tool_call`, `tool_result` | `text`, optional `label` | Optional tool activity |
  | `session` | `id` | Optional session identifier for the completed answer |
  | `error` | `text`, optional `code` | A failed request |

For a resumable JSONL profile, specify optional resume arguments with `{sessionId}`. SourceGit appends them when it has a saved ID and saves a returned ID after a successful answer. A command can report `{"type":"error","code":"session_expired","text":"…"}` to start one fresh session.

Chat session IDs are scoped to the worktree, conversation, profile and effective command arguments. Reopening a conversation can resume its session. Starting a new conversation or selecting a different model or effort starts a separate session. Commit generation uses its own session and resumes only when the profile's generation resume option is enabled.

## Examples

To check a profile without contacting an AI provider, choose `node` as the executable and enter these arguments in plain-text mode:

```text
-e
process.stdin.resume().on("end", () => console.log("fix: test the external generator"))
```

For a structured Codely profile, choose `node`, enable the prompt-file and JSONL options, and enter the following arguments. Replace the first two lines with absolute paths to the [adapter](examples/codely-jsonl.mjs) and the Codely JavaScript entry point from your installation. On npm installations, the Codely shim identifies its entry point.

```text
<absolute path to codely-jsonl.mjs>
<absolute path to the Codely CLI JavaScript entry point>
{promptFile}
--collaboration-mode
ask
--no-upm
--no-auto-edit-memory
```

Set the following resume arguments for that adapter:

```text
--resume-session
{sessionId}
```

The same app settings work with another vendor or a local backend by configuring its program directly or using a JSONL adapter for that backend. Profiles migrated from the older single-argument-string implementation show their previous arguments in the editor. Review the new input mode and enter any arguments you still need individually before enabling the profile's tasks.

## Generate a commit subject

Open the AI helper from the commit toolbar and select **CLI**. Generate a one-line subject from all staged changes, review it, then apply it to the commit editor. From the staged file list, select the files to include and use its AI menu to generate from that selection. When amending, SourceGit uses the original commit's parent as the baseline.

## Ask about changes

- Use **AI Chat** at the right end of the repository toolbar. SourceGit opens a side panel, or shows the same chat in a separate window when the repository view is too narrow.
- Right-click a commit, selected files or lines in a text diff. **AI → Add to AI Chat** attaches the selection to the active draft. **AI → Analysis** prepares the selection and a suggested question for a new conversation. Review or remove attachments before sending.
- Press **Enter** to send or **Shift+Enter** for a new line. Sending clears the composer and shows the question and attachments in the conversation. Follow-up questions use the frozen attachments. **Stop** cancels the current request. On cancellation or CLI failure, SourceGit restores the draft and its attachments. The external command may impose its own time limit.
- Answers support Markdown and code blocks. Select text across paragraphs or blocks to copy it. If the CLI emits reasoning or tool events, expand them in the conversation.
- **History** reopens saved conversations and drafts for this worktree, including after restarting SourceGit. The application configuration directory stores chat messages, drafts and attached Git snapshots. Tool activity and reasoning are available in the current app session.

### Choose a model and effort

In an external CLI profile, expand **AI Chat model and effort**. Enter supported model IDs or effort values one per line, then add the argument templates for that CLI. For example, a CLI may expect `--model={model}` and `--reasoning_effort={effort}`. Use that CLI's documented flags and values. Add each argument on its own line.

The corresponding selectors appear under the chat composer. **CLI default** leaves the override unset, and SourceGit remembers the selection per conversation. For commit generation, use the profile's main arguments or create a dedicated profile with the desired model and effort.

### Select a CLI skill

Expand **AI Chat skills** in an external CLI profile. **Skill list arguments** run the profile executable in the current worktree using these arguments in place of the main chat arguments. The list command must return a UTF-8 JSON array such as `[{"id":"code-review","name":"Code review","description":"Review a Git diff","source":"workspace"}]`. The `id` is required. The adapter should return enabled skills only.

In **Use skill arguments**, enter one argument per line and use `{skill}` where the selected ID should go. For the Codely adapter, use these list arguments:

```text
<absolute path to codely-jsonl.mjs>
<absolute path to the Codely CLI JavaScript entry point>
--list-skills
```

Enter `--skills` and `{skill}` on separate lines under **Use skill arguments**. Codely picks up the skill in the worktree. Its normal collaboration and approval options still apply.

Type `/` at the start of an empty composer and type to filter. Choose a skill, then enter your question. The composer shows the selected skill and clears it when you send. A failed or stopped request restores the skill along with the draft. History shows the skill on the question that used it.

## Create a branch and commit

In **Repository configuration → AI**, set the branch-name template. The default is `{type}/{slug}`. Generate a subject from the working-copy AI toolbar and review the branch name. **Create branch and commit** shows the branch, subject and staged file count before it writes to Git. If a hook rejects the commit, review the Git log and continue the commit on the new branch.
