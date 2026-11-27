// Optional adapter for Codely. Configure node as the executable in SourceGit.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const [entry, promptFile, ...options] = process.argv.slice(2);
const emit = event => console.log(JSON.stringify(event));
if (!entry || !promptFile) {
    emit({ type: 'error', text: 'Expected the Codely entry point and prompt file.' });
    process.exit(2);
}

const child = spawn(process.execPath, [entry, '-p', `@${promptFile}`, '--output-format', 'stream-json', ...options], {
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
});
let stderr = '';
let failure;
let delta = '';
let answer = '';
let session;
let userMessageSeen = false;
const tools = new Map();
child.stderr.setEncoding('utf8').on('data', text => { stderr = (stderr + text).slice(-8192); });
child.once('error', error => { failure = error.message; });
const exited = new Promise(resolve => child.once('close', resolve));

for await (const line of createInterface({ input: child.stdout, crlfDelay: Infinity })) {
    let event;
    try { event = JSON.parse(line); } catch { continue; }
    if (!event || typeof event !== 'object') continue;
    if (event.type === 'init' && typeof event.session_id === 'string') {
        session = event.session_id;
        emit({ type: 'session', id: session });
    } else if (event.type === 'message') {
        if (event.role === 'user') userMessageSeen = true;
        if (event.role !== 'assistant') continue;
        if (typeof event.reasoning_content === 'string' && event.reasoning_content)
            emit({ type: 'thinking', text: event.reasoning_content });
        const text = event.content;
        if (typeof text !== 'string' || !text ||
            (!userMessageSeen && session && text.startsWith(`resumed session ${session}, turns=`))) continue;
        if (event.delta) delta += text;
        else answer += text;
    } else if (['thinking', 'reasoning', 'agent_think'].includes(event.type)) {
        const text = event.thought ?? event.reasoning_content ?? event.content ?? event.text;
        if (typeof text === 'string' && text) emit({ type: 'thinking', text });
    } else if (['tool_use', 'tool_result', 'agent_tool_use', 'agent_tool_result'].includes(event.type)) {
        const { type, ...details } = event;
        if (event.tool_id && event.tool_name) tools.set(event.tool_id, event.tool_name);
        const label = [event.tool_name ?? tools.get(event.tool_id) ?? event.agent_name ?? event.tool_id, event.status]
            .filter(Boolean).join(' · ');
        emit({ type: type.endsWith('use') ? 'tool_call' : 'tool_result', label, text: JSON.stringify(details, null, 2) });
        if (type === 'tool_use' || type === 'tool_result') {
            delta = '';
            answer = '';
        }
    } else if (event.type === 'error' || (event.type === 'result' && event.status === 'error')) {
        failure ??= event.error?.message ?? event.error ?? event.message ?? 'Codely reported an error.';
    }
}

const code = await exited;
const text = (delta || answer).trim();
if (failure || code !== 0 || !text) {
    const why = failure == null ? stderr.trim() || 'Codely returned no answer.' : String(failure);
    const expired = /failed to restore startup resume history|no chat recording session found with id/i.test(why + stderr);
    emit({ type: 'error', code: expired ? 'session_expired' : 'cli_failed', text: why });
    process.exitCode = 1;
} else {
    emit({ type: 'answer', text });
}
