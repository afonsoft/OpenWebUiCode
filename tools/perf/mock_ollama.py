#!/usr/bin/env python3
"""Mock Ollama server for load/perf testing of Open WebUI (.NET).

Endpoints:
  GET  /api/tags        -> model list (mock:latest)
  POST /api/show        -> model card stub
  POST /api/chat        -> NDJSON stream (stream:true) or single JSON (stream:false)
  POST /api/embed       -> {"embeddings": [[...]]}
  POST /api/embeddings  -> {"embedding": [...]}
  GET  /api/version     -> {"version": "0.0.0-mock"}

Behavior triggers (looked up in the LAST message when role == "user"):
  __delegate__   -> tool_calls: builtin:delegate_task {"prompt": ..., "wait": false}
  __delegate_N__ -> N parallel delegate_task calls in one turn (wait=false)
  __words__N     -> stream N content pieces
  __sleep__MS    -> delay MS milliseconds before answering
  otherwise      -> short text answer streamed word by word (~2ms/piece)

Tool-call turn: after a role="tool" message the mock answers with plain text
so the agent loop finishes.
"""
import asyncio
import json
import re
import time
import uuid

from aiohttp import web

MODEL = "mock:latest"
EMB = [0.01 * i for i in range(64)]

DELEGATE_RE = re.compile(r"__delegate(?:_(\d+))?__")
WORDS_RE = re.compile(r"__words__(\d+)")
SLEEP_RE = re.compile(r"__sleep__(\d+)")


def _ts() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime())


def _chunk(content: str, done: bool = False, tool_calls=None) -> dict:
    msg = {"role": "assistant", "content": content}
    if tool_calls:
        msg["tool_calls"] = tool_calls
    return {
        "model": MODEL,
        "created_at": _ts(),
        "message": msg,
        "done": done,
    }


def _delegate_calls(n: int) -> list:
    return [
        {
            "id": f"call-{uuid.uuid4().hex[:8]}",
            "function": {
                "name": "builtin:delegate_task",
                "arguments": {
                    "prompt": f"Subtask {i}: reply with a short confirmation.",
                    "wait": False,
                },
            },
        }
        for i in range(n)
    ]


def _plan(messages: list):
    """Decide what the mock answers. Returns (kind, payload)."""
    last_user = ""
    last_role = ""
    for m in reversed(messages):
        last_role = m.get("role", "")
        if last_role == "user":
            last_user = m.get("content") or ""
            break
    # tool result just came back -> finish with text
    if messages and messages[-1].get("role") == "tool":
        return ("text", "Tool result processed. Done.", 0)
    if messages and messages[-1].get("role") == "user" and not last_user:
        last_user = messages[-1].get("content") or ""
    sleep = SLEEP_RE.search(last_user)
    delay_ms = int(sleep.group(1)) if sleep else 0
    dlg = DELEGATE_RE.search(last_user)
    if dlg:
        n = int(dlg.group(1) or "1")
        return ("tool_calls", _delegate_calls(min(n, 8)), delay_ms)
    words = WORDS_RE.search(last_user)
    n_words = int(words.group(1)) if words else 6
    return ("text", " ".join(f"w{i}" for i in range(n_words)), delay_ms)


async def tags(request: web.Request) -> web.Response:
    return web.json_response(
        {
            "models": [
                {
                    "name": MODEL,
                    "model": MODEL,
                    "modified_at": _ts(),
                    "size": 1024,
                    "digest": "mock",
                    "details": {
                        "format": "gguf",
                        "family": "mock",
                        "families": ["mock"],
                        "parameter_size": "1B",
                        "quantization_level": "Q4",
                    },
                }
            ]
        }
    )


async def show(request: web.Request) -> web.Response:
    return web.json_response(
        {"modelfile": "", "parameters": "", "template": "", "details": {}}
    )


async def version(request: web.Request) -> web.Response:
    return web.json_response({"version": "0.0.0-mock"})


async def embed(request: web.Request) -> web.Response:
    body = await request.json()
    inp = body.get("input", "")
    n = len(inp) if isinstance(inp, list) else 1
    return web.json_response({"embeddings": [EMB] * max(n, 1)})


async def embeddings(request: web.Request) -> web.Response:
    return web.json_response({"embedding": EMB})


async def chat(request: web.Request) -> web.Response:
    body = await request.json()
    messages = body.get("messages", [])
    stream = body.get("stream", True)
    kind, payload, delay_ms = _plan(messages)
    if delay_ms:
        await asyncio.sleep(delay_ms / 1000.0)

    if kind == "tool_calls":
        calls = payload
        if not stream:
            return web.json_response(
                {
                    "model": MODEL,
                    "created_at": _ts(),
                    "message": {
                        "role": "assistant",
                        "content": "",
                        "tool_calls": calls,
                    },
                    "done": True,
                    "done_reason": "stop",
                }
            )
        resp = web.StreamResponse(
            status=200,
            headers={"Content-Type": "application/x-ndjson"},
        )
        await resp.prepare(request)
        line = _chunk("", done=True, tool_calls=calls)
        line["done_reason"] = "stop"
        await resp.write((json.dumps(line) + "\n").encode())
        await resp.write_eof()
        return resp

    text = payload
    pieces = text.split(" ")
    if not stream:
        return web.json_response(
            {
                "model": MODEL,
                "created_at": _ts(),
                "message": {"role": "assistant", "content": text},
                "done": True,
                "done_reason": "stop",
            }
        )
    resp = web.StreamResponse(
        status=200, headers={"Content-Type": "application/x-ndjson"}
    )
    await resp.prepare(request)
    for p in pieces:
        await resp.write((json.dumps(_chunk(p + " ")) + "\n").encode())
        await asyncio.sleep(0.002)
    final = _chunk("", done=True)
    final["done_reason"] = "stop"
    final["total_duration"] = 1000
    await resp.write((json.dumps(final) + "\n").encode())
    await resp.write_eof()
    return resp


def main() -> None:
    app = web.Application()
    app.router.add_get("/api/tags", tags)
    app.router.add_post("/api/show", show)
    app.router.add_get("/api/version", version)
    app.router.add_post("/api/chat", chat)
    app.router.add_post("/api/embed", embed)
    app.router.add_post("/api/embeddings", embeddings)
    print("mock-ollama listening on 127.0.0.1:11434", flush=True)
    web.run_app(app, host="127.0.0.1", port=11434, print=None)


if __name__ == "__main__":
    main()
