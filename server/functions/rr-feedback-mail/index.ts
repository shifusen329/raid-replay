// Emails one Raid Replay feedback report (public.rr_feedback) to SMTP_ADMIN_EMAIL.
//
// Called with {"id": <report id>} by the table's insert trigger (pg_net) and by the pg_cron retry job; never exposed
// by nginx. The report is claimed first (rr_feedback_claim), so overlapping calls send it once. The pull's log and the
// report data are attached.
//
// Needs SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS and SMTP_ADMIN_EMAIL in the functions container's environment.

import { serve } from "https://deno.land/std@0.177.1/http/server.ts";
import nodemailer from "npm:nodemailer@6.9.16";

const rpc = `${Deno.env.get("SUPABASE_URL")}/rest/v1/rpc`;
const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? "";
const to = Deno.env.get("SMTP_ADMIN_EMAIL") ?? "";
const port = Number(Deno.env.get("SMTP_PORT") ?? 587);

// 465 is TLS from the start; anything else (587) must upgrade with STARTTLS before logging in.
const smtp = nodemailer.createTransport({
  host: Deno.env.get("SMTP_HOST"),
  port,
  secure: port === 465,
  requireTLS: port !== 465,
  auth: { user: Deno.env.get("SMTP_USER"), pass: Deno.env.get("SMTP_PASS") },
});

const categories: Record<string, string> = {
  wrong_culprit: "Wrong culprit",
  wrong_root_cause: "Wrong root cause",
  missed_mistake: "Missed mistake",
  wrong_spot: "Wrong position/spot",
  other: "Other",
};

async function call(name: string, args: Record<string, unknown>) {
  const res = await fetch(`${rpc}/${name}`, {
    method: "POST",
    headers: { apikey: serviceKey, Authorization: `Bearer ${serviceKey}`, "Content-Type": "application/json" },
    body: JSON.stringify(args),
  });
  const text = await res.text();
  if (!res.ok)
    throw new Error(`${name}: ${res.status} ${text}`);
  return text ? JSON.parse(text) : null;
}

const duration = (ms: number) => `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, "0")}`;

function pullLine(p: Record<string, any>): string {
  return [
    p.ordinal != null ? `Pull #${p.ordinal}` : "Pull",
    p.encounter ?? p.zone,
    p.phase,
    p.durationMs != null ? `${duration(p.durationMs)} ${String(p.outcome ?? "").toLowerCase()}`.trim() : null,
    p.bossHpPct != null ? `boss ${p.bossHpPct}%` : null,
    p.deaths != null ? `${p.deaths} death${p.deaths === 1 ? "" : "s"}` : null,
  ].filter((x) => x).join(" · ");
}

function incidentLines(i: Record<string, any> | null): string[] {
  if (!i)
    return ["About: the report as a whole"];
  const lines = [`Incident ${i.time} · ${i.kind}${i.mechanic ? ` · ${i.mechanic}` : ""}${i.root ? " (root cause)" : ""}`, i.title];
  const who = [i.atFault?.length ? `At fault: ${i.atFault.join(", ")}` : null, i.victim ? `victim: ${i.victim}` : null]
    .filter((x) => x).join(" · ");
  if (who)
    lines.push(who);
  if (i.detail)
    lines.push(i.detail);
  for (const m of i.misses ?? [])
    lines.push(`  ${m.slot}: ${m.missY}y off (${m.source}${m.note ? `: ${m.note}` : ""})`);
  return lines;
}

function body(r: Record<string, any>): string {
  const pull = r.pull ?? {};
  const lines = [
    categories[r.category] ?? r.category,
    pullLine(pull),
  ];
  if (pull.verdict)
    lines.push(`Verdict: ${pull.verdict}`);
  lines.push("", ...incidentLines(r.incident), "", "Note:", r.note?.trim() || "(none)", "");
  lines.push(
    `Report #${r.id} · received ${new Date(r.received_at).toISOString().replace("T", " ").slice(0, 19)} UTC · plugin ${r.plugin_version}` +
      (r.pack ? ` · pack ${r.pack}` : "") + (r.install_id ? ` · install ${String(r.install_id).slice(0, 8)}` : "") +
      (r.client_ip ? ` · IP ${r.client_ip}` : ""),
  );
  if (r.log_gz_base64)
    lines.push(`Attached: the pull's log (names replaced by Player1–8; report.players maps them to slots) and the full report.`);
  return lines.join("\n");
}

serve(async (req) => {
  if (req.method !== "POST")
    return new Response("POST only", { status: 405 });
  const { id } = await req.json().catch(() => ({}));
  if (!Number.isInteger(id))
    return new Response("id required", { status: 400 });

  const [report] = (await call("rr_feedback_claim", { p_id: id })) ?? [];
  if (!report)
    return new Response("nothing to send", { status: 200 });

  try {
    const pull = report.pull ?? {};
    const attachments = [];
    if (report.report)
      attachments.push({ filename: `rr-feedback-${id}-report.json`, contentType: "application/json",
                         content: JSON.stringify(report.report, null, 2) });
    if (report.log_gz_base64)
      attachments.push({ filename: `rr-feedback-${id}.log.gz`, contentType: "application/gzip", encoding: "base64",
                         content: report.log_gz_base64 });

    await smtp.sendMail({
      from: `Raid Replay feedback <${to}>`,
      to,
      subject: `[Raid Replay] ${categories[report.category] ?? report.category}` +
        (pull.ordinal != null ? ` · pull #${Number(pull.ordinal)}` : "") + (pull.verdict ? ` · ${String(pull.verdict).slice(0, 80)}` : ""),
      text: body(report),
      attachments,
    });

    await call("rr_feedback_mailed", { p_id: id });
    return new Response("sent", { status: 200 });
  } catch (e) {
    const error = e instanceof Error ? e.message : String(e);
    await call("rr_feedback_mailed", { p_id: id, p_error: error.slice(0, 2000) }).catch(() => {});
    return new Response(error, { status: 502 });
  }
});
