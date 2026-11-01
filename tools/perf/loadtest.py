#!/usr/bin/env python3
"""Load/perf harness for Open WebUI (.NET) — repo: afonsoft/OpenWebUiCode.

Exercises the API under load with an async client and measures throughput,
latency percentiles and error rates.

Scenarios
  rps       Sustained HTTP throughput (target >= 30 req/s): a weighted mix of
            hot endpoints — create chat, list chats, get chat, enqueue run
            message, list runs, attach SSE stream.
  sessions  U users x C chats holding concurrent active runs: each chat loops
            enqueue -> SSE attach -> run finished -> next round.
  subagents N chats each send a message that makes the mock emit
            builtin:delegate_task calls with wait=false; the harness
            auto-approves tool gates and counts spawned/completed child runs.
  real      Same shape as `sessions`, smaller scale, meant for a real
            provider model (e.g. OmniRoute via OPENAI_API_BASE_URL).

Usage
  python3 tools/perf/loadtest.py --base http://localhost:8080 \
      --email perf@load.test --password loadtest123 \
      --model mock:latest --scenario all \
      --json /tmp/loadtest-metrics.json --md docs/perf/loadtest-report.md
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import random
import statistics
import time
from dataclasses import dataclass, field
from typing import Any

import aiohttp

# S2245: SystemRandom é CSPRNG (os.urandom) — aceito pelo Sonar mesmo num
# contexto não-sensível como este gerador de carga.
_sysrand = random.SystemRandom()


@dataclass
class Sample:
    group: str
    status: int
    ms: float
    err: str | None = None
    t: float = 0.0


@dataclass
class Metrics:
    samples: list[Sample] = field(default_factory=list)
    started: float = field(default_factory=time.time)
    counters: dict[str, int] = field(default_factory=dict)
    windows: dict[str, tuple[float, float]] = field(default_factory=dict)

    def add(self, group: str, status: int, ms: float, err: str | None = None) -> None:
        self.samples.append(Sample(group, status, ms, err, time.time()))
        if status >= 400 or err:
            key = f"{group}:{status if status else 'err'}"
            self.counters[key] = self.counters.get(key, 0) + 1

    def count(self, key: str) -> int:
        return self.counters.get(key, 0)

    def bump(self, key: str, n: int = 1) -> None:
        self.counters[key] = self.counters.get(key, 0) + n

    def summary(self, group: str | None = None) -> dict[str, Any]:
        rows = [s for s in self.samples if group is None or s.group == group]
        if not rows:
            return {"count": 0}
        lat = sorted(s.ms for s in rows)

        def pct(p: float) -> float:
            idx = min(len(lat) - 1, max(0, int(round(p * (len(lat) - 1)))))
            return round(lat[idx], 1)

        errors = sum(1 for s in rows if s.status >= 400 or s.err)
        return {
            "count": len(rows),
            "errors": errors,
            "error_pct": round(100.0 * errors / len(rows), 2),
            "mean_ms": round(statistics.fmean(lat), 1),
            "p50_ms": pct(0.50),
            "p90_ms": pct(0.90),
            "p95_ms": pct(0.95),
            "p99_ms": pct(0.99),
            "max_ms": round(lat[-1], 1),
            "min_ms": round(lat[0], 1),
        }


class Api:
    def __init__(self, base: str, metrics: Metrics) -> None:
        self.base = base.rstrip("/")
        self.metrics = metrics

    async def req(
        self,
        session: aiohttp.ClientSession,
        group: str,
        method: str,
        path: str,
        token: str | None = None,
        json_body: Any = None,
    ) -> tuple[int, Any]:
        url = self.base + path
        headers = {"Authorization": f"Bearer {token}"} if token else {}
        t0 = time.perf_counter()
        try:
            async with session.request(
                method, url, headers=headers, json=json_body
            ) as resp:
                body = await resp.read()
                ms = (time.perf_counter() - t0) * 1000
                self.metrics.add(group, resp.status, ms)
                try:
                    data = json.loads(body.decode())
                except Exception:
                    data = None
                return resp.status, data
        except Exception as exc:  # noqa: BLE001
            ms = (time.perf_counter() - t0) * 1000
            self.metrics.add(group, 0, ms, err=str(exc))
            return 0, None

    async def signup(
        self, session: aiohttp.ClientSession, name: str, email: str, password: str
    ) -> str | None:
        status, data = await self.req(
            session, "auth", "POST", "/api/v1/auths/signin",
            json_body={"email": email, "password": password},
        )
        if status == 200 and data and data.get("token"):
            return data["token"]
        status, data = await self.req(
            session, "auth", "POST", "/api/v1/auths/signup",
            json_body={"name": name, "email": email, "password": password},
        )
        if status == 200 and data and data.get("token"):
            return data["token"]
        return None

    async def ensure_user(
        self,
        session: aiohttp.ClientSession,
        admin_token: str,
        name: str,
        email: str,
        password: str,
    ) -> str | None:
        """Cria usuário e aprova (role pending -> user) via admin."""
        token = await self.signup(session, name, email, password)
        if token:
            return token
        # signup retornou pending (token vazio) ou email_taken: busca o id
        # na lista de pendentes e aprova via admin
        _, users = await self.req(
            session, "user_list", "GET",
            "/api/v1/users/?filter=pending&per_page=100", admin_token,
        )
        if isinstance(users, dict):
            rows = users.get("users", [])
        elif isinstance(users, list):
            rows = users
        else:
            rows = []
        user_id = next(
            (u.get("id") for u in rows if u.get("email") == email), None)
        if not user_id:
            return None
        await self.req(
            session, "user_approve", "POST",
            f"/api/v1/users/{user_id}/update/role", admin_token,
            json_body={"role": "user"},
        )
        return await self.signup(session, name, email, password)

    async def create_chat(
        self, session: aiohttp.ClientSession, token: str, title: str
    ) -> str | None:
        status, data = await self.req(
            session, "chat_create", "POST", "/api/v1/chats", token,
            json_body={"title": title, "models": [], "messages": []},
        )
        if status == 200 and data:
            return data.get("id")
        return None

    async def enqueue(
        self,
        session: aiohttp.ClientSession,
        token: str,
        chat_id: str,
        content: str,
        model: str,
        tool_ids: list[str] | None = None,
    ) -> str | None:
        body: dict[str, Any] = {"content": content, "model": model}
        if tool_ids:
            body["toolIds"] = tool_ids
        status, data = await self.req(
            session, "enqueue", "POST",
            f"/api/v1/chats/{chat_id}/messages", token, json_body=body,
        )
        if status == 202 and data:
            return data.get("id")
        return None

    async def get_run(
        self, session: aiohttp.ClientSession, token: str,
        chat_id: str, run_id: str,
    ) -> dict | None:
        status, data = await self.req(
            session, "run_get", "GET",
            f"/api/v1/chats/{chat_id}/runs/{run_id}", token,
        )
        return data if status == 200 else None

    async def approve(
        self, session: aiohttp.ClientSession, token: str,
        chat_id: str, run_id: str, call_id: str,
    ) -> None:
        await self.req(
            session, "approve", "POST",
            f"/api/v1/chats/{chat_id}/runs/{run_id}/approvals/{call_id}",
            token,
            json_body={"decision": "approve", "remember": True},
        )

    async def list_all_runs(
        self, session: aiohttp.ClientSession, token: str
    ) -> list[dict]:
        status, data = await self.req(
            session, "runs_list", "GET", "/api/v1/chats/runs", token,
        )
        return data if status == 200 and isinstance(data, list) else []


async def _drain_sse_events(
    api: Api,
    session: aiohttp.ClientSession,
    token: str,
    chat_id: str,
    run_id: str,
    resp: aiohttp.ClientResponse,
    metrics: Metrics,
    t0: float,
    timeout_s: float,
) -> str | None:
    """Consume o corpo SSE e despacha eventos. Retorna 'timeout' ou None."""
    event = None
    data_lines: list[str] = []
    async for raw in resp.content:
        line = raw.decode("utf-8", "replace").rstrip("\n")
        if line == "":
            if event and data_lines:
                await handle_sse_event(
                    api, session, token, chat_id, run_id,
                    event, "\n".join(data_lines), metrics,
                )
            event = None
            data_lines = []
            continue
        if line.startswith(":"):
            continue
        if line.startswith("event:"):
            event = line[6:].strip()
        elif line.startswith("data:"):
            data_lines.append(line[5:].strip())
        if time.perf_counter() - t0 > timeout_s:
            return "timeout"
    # flush pending event
    if event and data_lines:
        await handle_sse_event(
            api, session, token, chat_id, run_id,
            event, "\n".join(data_lines), metrics,
        )
    return None


async def stream_run(
    api: Api,
    session: aiohttp.ClientSession,
    token: str,
    chat_id: str,
    run_id: str,
    metrics: Metrics,
    timeout_s: float = 120.0,
) -> str:
    """Attach to the run SSE stream; auto-approves tool gates; returns final
    run status (completed|failed|stopped|timeout|error)."""
    url = f"{api.base}/api/v1/chats/{chat_id}/runs/{run_id}/stream?lastSeq=0"
    headers = {"Authorization": f"Bearer {token}"}
    t0 = time.perf_counter()
    try:
        async with session.get(
            url, headers=headers, timeout=aiohttp.ClientTimeout(total=timeout_s)
        ) as resp:
            metrics.add("sse_attach", resp.status, (time.perf_counter() - t0) * 1000)
            if resp.status != 200:
                return f"sse_{resp.status}"
            outcome = await _drain_sse_events(
                api, session, token, chat_id, run_id, resp,
                metrics, t0, timeout_s,
            )
            if outcome:
                return outcome
            run = await api.get_run(session, token, chat_id, run_id)
            return (run or {}).get("status", "unknown")
    except asyncio.TimeoutError:
        return "timeout"
    except Exception as exc:  # noqa: BLE001
        metrics.add("sse_attach", 0, (time.perf_counter() - t0) * 1000, err=str(exc))
        return "error"


async def handle_sse_event(
    api: Api,
    session: aiohttp.ClientSession,
    token: str,
    chat_id: str,
    run_id: str,
    event: str,
    data: str,
    metrics: Metrics,
) -> None:
    metrics.bump(f"sse_event:{event}")
    if event != "approval_asked":
        return
    try:
        payload = json.loads(data)
        call_id = payload.get("callId")
    except Exception:  # noqa: BLE001
        return
    if call_id:
        await api.approve(session, token, chat_id, run_id, call_id)
        metrics.bump("approvals")


async def _rps_dispatch(
    api: Api,
    session: aiohttp.ClientSession,
    token: str,
    kind: str,
    cid: str,
    counter: int,
    model: str,
    pool: list[str],
) -> None:
    if kind == "list_chats":
        await api.req(session, "list_chats", "GET", "/api/v1/chats", token)
    elif kind == "get_chat":
        await api.req(session, "get_chat", "GET", f"/api/v1/chats/{cid}", token)
    elif kind == "models":
        await api.req(session, "models", "GET", "/api/models", token)
    elif kind == "mcp_servers":
        # HybridCache: lista de servidores MCP (60s, tag "mcp").
        await api.req(
            session, "mcp_servers", "GET", "/api/v1/mcp/servers", token)
    elif kind == "config_admin":
        # ConfigService via HybridCache (10s, tag "config").
        await api.req(
            session, "config_admin", "GET",
            "/api/v1/auths/admin/config", token)
    elif kind == "runs_list":
        await api.req(session, "runs_list", "GET", "/api/v1/chats/runs", token)
    elif kind == "runs_active":
        await api.req(
            session, "runs_active", "GET",
            f"/api/v1/chats/{cid}/runs/active", token)
    elif kind == "chat_create":
        new_id = await api.create_chat(session, token, f"perf-rps-extra-{counter}")
        if new_id:
            pool.append(new_id)
    elif kind == "enqueue":
        await api.enqueue(
            session, token, cid, f"ping {counter} __words__8", model)


async def _queue_monitor(
    api: Api,
    session: aiohttp.ClientSession,
    token: str,
    metrics: Metrics,
    duration: float,
) -> None:
    """Amostra a fila a cada 2s até o Event de deadline disparar."""
    seen_completed = 0
    max_pending = 0
    stop = asyncio.Event()
    timer = asyncio.get_running_loop().call_later(duration, stop.set)
    try:
        while not stop.is_set():
            runs = await api.list_all_runs(session, token)
            pending = sum(
                1 for r in runs
                if r.get("status") in ("queued", "running", "paused"))
            completed = sum(1 for r in runs if r.get("status") == "completed")
            seen_completed = max(seen_completed, completed)
            max_pending = max(max_pending, pending)
            metrics.bump("queue_samples")
            try:
                await asyncio.wait_for(stop.wait(), 2.0)
            except TimeoutError:
                pass
    finally:
        timer.cancel()
    metrics.bump("queue_max_pending", max_pending)
    metrics.bump("queue_completed_in_window", seen_completed)


async def scenario_rps(
    api: Api, args: argparse.Namespace, session: aiohttp.ClientSession,
    token: str, metrics: Metrics,
) -> None:
    """Weighted mix of hot endpoints for `args.duration` seconds."""
    pool: list[str] = []
    for i in range(args.chats_pool):
        cid = await api.create_chat(session, token, f"perf-rps-{i}")
        if cid:
            pool.append(cid)
    if not pool:
        raise RuntimeError("não consegui criar chats para o pool")

    weights = [
        ("list_chats", 3), ("get_chat", 2), ("enqueue", 4),
        ("runs_active", 1), ("models", 2), ("runs_list", 1), ("chat_create", 1),
        ("mcp_servers", 1), ("config_admin", 1),
    ]
    names, probs = zip(*weights)
    deadline = time.time() + args.duration
    counter = 0

    monitor = asyncio.create_task(
        _queue_monitor(api, session, token, metrics, args.duration))

    async def worker() -> None:
        nonlocal counter
        while time.time() < deadline:
            counter += 1
            await _rps_dispatch(
                api, session, token,
                _sysrand.choices(names, probs)[0], _sysrand.choice(pool),
                counter, args.model, pool)

    workers = [asyncio.create_task(worker()) for _ in range(args.workers)]
    await asyncio.gather(*workers)
    monitor.cancel()
    try:
        await monitor
    except asyncio.CancelledError:
        if not monitor.cancelled():
            raise


async def chat_session_loop(
    api: Api, session: aiohttp.ClientSession, token: str,
    chat_id: str, model: str, rounds: int, metrics: Metrics,
    delay: float = 0.0,
) -> None:
    """One active chat: enqueue -> attach -> done -> next round."""
    for r in range(rounds):
        t0 = time.perf_counter()
        run_id = await api.enqueue(
            session, token, chat_id, f"session round {r} __words__10", model)
        if not run_id:
            metrics.bump("run_failed_enqueue")
            continue
        status = await stream_run(
            api, session, token, chat_id, run_id, metrics)
        ms = (time.perf_counter() - t0) * 1000
        metrics.add("run_e2e", 200 if status == "completed" else 500, ms,
                    err=None if status == "completed" else f"status={status}")
        metrics.bump(f"run_status:{status}")
        if delay:
            await asyncio.sleep(delay)


async def scenario_sessions(
    api: Api, session: aiohttp.ClientSession,
    admin_token: str, metrics: Metrics, model: str,
    users: int, chats_per_user: int, rounds: int,
) -> None:
    """U users x C chats running concurrent active runs."""
    tokens: list[str] = [admin_token]
    for u in range(users - 1):
        email = f"perf-user-{u}@load.test"
        t = await api.ensure_user(
            session, admin_token, f"Perf User {u}", email, "loadtest123")
        if t:
            tokens.append(t)
    tasks = []
    for u, tok in enumerate(tokens):
        for c in range(chats_per_user):
            cid = await api.create_chat(
                session, tok, f"perf-sess-u{u}-c{c}")
            if cid:
                tasks.append(
                    chat_session_loop(
                        api, session, tok, cid, model, rounds, metrics))
    await asyncio.gather(*tasks)


async def _subagent_one(
    api: Api,
    session: aiohttp.ClientSession,
    token: str,
    cid: str,
    w: int,
    args: argparse.Namespace,
    metrics: Metrics,
) -> None:
    t0 = time.perf_counter()
    run_id = await api.enqueue(
        session, token, cid,
        f"wave {w}: delegate __delegate_{args.delegates_per_chat}__",
        args.model, tool_ids=["builtin:delegate_task"])
    if not run_id:
        metrics.bump("subagent_failed_enqueue")
        return
    status = await stream_run(
        api, session, token, cid, run_id, metrics,
        timeout_s=args.subagent_timeout)
    ms = (time.perf_counter() - t0) * 1000
    metrics.add("subagent_parent", 200 if status == "completed" else 500,
                ms, err=None if status == "completed" else f"status={status}")
    metrics.bump(f"subagent_parent_status:{status}")


async def _settle_children(
    api: Api,
    session: aiohttp.ClientSession,
    token: str,
    timeout_s: float,
) -> list[dict]:
    """Espera os runs-filho (parentRunId) drenarem; retorna a lista final."""
    settle_deadline = time.time() + timeout_s
    children: list[dict] = []
    while time.time() < settle_deadline:
        runs = await api.list_all_runs(session, token)
        children = [r for r in runs if r.get("parentRunId")]
        pending = [r for r in children
                   if r.get("status") in ("queued", "running", "paused")]
        if not pending and children:
            break
        await asyncio.sleep(1.0)
    return children


async def scenario_subagents(
    api: Api, args: argparse.Namespace, session: aiohttp.ClientSession,
    token: str, metrics: Metrics,
) -> None:
    """N chats each fan out delegate_task wait=false child runs."""
    chats: list[str] = []
    for i in range(args.fanout):
        cid = await api.create_chat(session, token, f"perf-sub-{i}")
        if cid:
            chats.append(cid)
    if not chats:
        raise RuntimeError("não consegui criar chats para subagents")

    # delegate_task é workspace-gated: no modo chat (default) o ruleset nega a
    # tool e nenhum filho spawna. PATCH mode=build exige binding prévio
    # (ChatEndpoints.PatchChatAsync rejeita build sem repo) — então o PUT de
    # workspace-repo precisa vir primeiro, seguido do preset que auto-aprova.
    repo = os.environ.get("LT_REPO_SLUG") or "afonsoft/OpenWebUiCode"
    branch = os.environ.get("LT_REPO_BRANCH") or "main"
    for cid in chats:
        await api.req(
            session, "chat_bind", "PUT",
            f"/api/v1/chats/{cid}/workspace-repo", token,
            json_body={"repo": repo, "branch": branch})
        await api.req(
            session, "chat_patch", "PATCH", f"/api/v1/chats/{cid}", token,
            json_body={"mode": "build", "approvalPreset": "always-allow"})

    t_wave = time.perf_counter()
    for w in range(args.waves):
        await asyncio.gather(
            *(_subagent_one(api, session, token, c, w, args, metrics)
              for c in chats))
    wave_ms = (time.perf_counter() - t_wave) * 1000
    metrics.bump("subagent_wave_ms_total", int(wave_ms))

    # settle: wait for all child runs to finish
    children = await _settle_children(
        api, session, token, args.subagent_timeout)
    done = sum(1 for r in children if r.get("status") == "completed")
    failed = sum(1 for r in children if r.get("status") not in ("completed",))
    metrics.bump("subagent_children_spawned", len(children))
    metrics.bump("subagent_children_completed", done)
    metrics.bump("subagent_children_failed", failed)


def render_report(args: argparse.Namespace, metrics: Metrics,
                  scenario_names: list[str]) -> str:
    total_s = time.time() - metrics.started
    lines = [
        "# Load test report — Open WebUI (.NET)",
        "",
        f"- Base URL: `{args.base}`",
        f"- Model: `{args.model}`" + (
            f" (real: `{args.real_model}`)" if args.real_model else ""),
        f"- Date: {time.strftime('%Y-%m-%d %H:%M:%S UTC', time.gmtime())}",
        f"- Scenarios: {', '.join(scenario_names)}",
        f"- Total wall time: {total_s:.1f}s",
        f"- Total requests sampled: {len(metrics.samples)}",
        f"- Aggregate rps: {len(metrics.samples) / max(total_s, 0.001):.1f}",
        "",
        "## Aggregate metrics",
        "",
        "| metric | value |",
        "|---|---|",
    ]
    agg = metrics.summary()
    for k, v in agg.items():
        lines.append(f"| {k} | {v} |")
    if metrics.windows:
        lines += ["", "## Per-scenario", "",
                  "| scenario | wall s | requests | rps | errors |",
                  "|---|---|---|---|---|"]
        for name, (t0, t1) in metrics.windows.items():
            in_win = [s for s in metrics.samples if t0 <= s.t <= t1]
            errs = sum(1 for s in in_win if s.status >= 400 or s.err)
            dur = max(t1 - t0, 0.001)
            lines.append(
                f"| {name} | {dur:.1f} | {len(in_win)} "
                f"| {len(in_win) / dur:.1f} | {errs} |")
    lines += ["", "## Per-endpoint / per-operation", "",
              "| group | count | errors | err% | mean ms | p50 | p95 | p99 | max |",
              "|---|---|---|---|---|---|---|---|---|"]
    groups = sorted({s.group for s in metrics.samples})
    for g in groups:
        s = metrics.summary(g)
        lines.append(
            f"| {g} | {s['count']} | {s['errors']} | {s['error_pct']} "
            f"| {s['mean_ms']} | {s['p50_ms']} | {s['p95_ms']} "
            f"| {s['p99_ms']} | {s['max_ms']} |")
    lines += ["", "## Counters", "", "| counter | value |", "|---|---|"]
    for k in sorted(metrics.counters):
        lines.append(f"| {k} | {metrics.counters[k]} |")
    lines.append("")
    return "\n".join(lines)


async def settle_queue(
    api: Api, session: aiohttp.ClientSession, token: str,
    timeout_s: float = 90.0,
) -> int:
    """Espera a fila do dispatcher drenar entre cenários."""
    deadline = time.time() + timeout_s
    pending = -1
    while time.time() < deadline:
        runs = await api.list_all_runs(session, token)
        pending = sum(
            1 for r in runs
            if r.get("status") in ("queued", "running", "paused"))
        if pending == 0:
            return 0
        await asyncio.sleep(2.0)
    return pending


async def run(args: argparse.Namespace) -> Metrics:
    metrics = Metrics()
    api = Api(args.base, metrics)
    conn = aiohttp.TCPConnector(limit=0, limit_per_host=0, ttl_dns_cache=300)
    timeout = aiohttp.ClientTimeout(total=120, connect=10)
    async with aiohttp.ClientSession(
        connector=conn, timeout=timeout
    ) as session:
        token = await api.signup(
            session, "Perf Admin", args.email, args.password)
        if not token:
            raise RuntimeError("auth failed — nem signin nem signup funcionaram")
        scenarios = (
            ["rps", "sessions", "subagents"]
            if args.scenario == "all"
            else args.scenario.split(",")
        )
        done = []
        if "rps" in scenarios:
            t0 = time.time()
            await scenario_rps(api, args, session, token, metrics)
            metrics.windows["rps"] = (t0, time.time())
            done.append("rps")
            metrics.bump("leftover_queue_after_rps",
                         await settle_queue(api, session, token))
        if "sessions" in scenarios:
            t0 = time.time()
            await scenario_sessions(
                api, session, token, metrics, args.model,
                users=args.users, chats_per_user=args.chats_per_user,
                rounds=args.rounds)
            metrics.windows["sessions"] = (t0, time.time())
            done.append("sessions")
        if "subagents" in scenarios:
            t0 = time.time()
            await scenario_subagents(api, args, session, token, metrics)
            metrics.windows["subagents"] = (t0, time.time())
            done.append("subagents")
        if "real" in scenarios and args.real_model:
            t0 = time.time()
            await scenario_sessions(
                api, session, token, metrics, args.real_model,
                users=args.real_users, chats_per_user=args.real_chats_per_user,
                rounds=args.real_rounds)
            metrics.windows["real"] = (t0, time.time())
            done.append("real")
        args._done = done
        return metrics


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--base", default="http://localhost:8080")
    p.add_argument("--email", default="perf@load.test")
    p.add_argument("--password", default="loadtest123")
    p.add_argument("--model", default="mock:latest")
    p.add_argument("--real-model", default=None)
    p.add_argument("--scenario", default="all",
                   help="all ou lista: rps,sessions,subagents,real")
    p.add_argument("--duration", type=int, default=30,
                   help="segundos do cenário rps")
    p.add_argument("--workers", type=int, default=16)
    p.add_argument("--chats-pool", type=int, default=24)
    p.add_argument("--users", type=int, default=4)
    p.add_argument("--chats-per-user", type=int, default=3)
    p.add_argument("--rounds", type=int, default=3)
    p.add_argument("--fanout", type=int, default=6,
                   help="chats disparando delegate_task")
    p.add_argument("--delegates-per-chat", type=int, default=2)
    p.add_argument("--waves", type=int, default=2)
    p.add_argument("--subagent-timeout", type=float, default=120.0)
    p.add_argument("--real-users", type=int, default=2)
    p.add_argument("--real-chats-per-user", type=int, default=2)
    p.add_argument("--real-rounds", type=int, default=1)
    p.add_argument("--json", default=None)
    p.add_argument("--md", default=None)
    args = p.parse_args()

    metrics = asyncio.run(run(args))
    report = render_report(args, metrics, getattr(args, "_done", []))
    print(report)
    for out in (args.md, args.json):
        if out:
            os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    if args.md:
        with open(args.md, "w") as f:
            f.write(report)
    if args.json:
        payload = {
            "base": args.base,
            "model": args.model,
            "windows": {k: list(v) for k, v in metrics.windows.items()},
            "samples": [s.__dict__ for s in metrics.samples],
            "counters": metrics.counters,
        }
        with open(args.json, "w") as f:
            json.dump(payload, f)


if __name__ == "__main__":
    main()
