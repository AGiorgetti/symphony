namespace Symphony.DotNet.Services;

internal static class DashboardAssets
{
    public const string Css = """
:root {
  --bg: #0f172a;
  --panel: #111827;
  --text: #e5e7eb;
  --muted: #94a3b8;
  --border: #334155;
  --link: #7dd3fc;
}
body {
  margin: 0;
  background: radial-gradient(circle at top, #1e293b, var(--bg));
  color: var(--text);
  font-family: Segoe UI, Helvetica, Arial, sans-serif;
}
a { color: var(--link); }
.shell { max-width: 1200px; margin: 0 auto; padding: 24px; }
.hero, .panel, .metric { background: rgba(17,24,39,.92); border: 1px solid var(--border); border-radius: 18px; }
.hero { padding: 24px; margin-bottom: 20px; }
.hero h1 { margin: 0 0 8px; font-size: 2rem; }
.hero p { margin: 0; color: var(--muted); }
.metrics { display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; margin-bottom: 20px; }
.metric { padding: 18px; }
.metric .label { color: var(--muted); font-size: .9rem; margin-bottom: 8px; }
.metric .value { font-size: 1.8rem; font-weight: 700; }
.panel { padding: 20px; margin-bottom: 20px; }
.table { width: 100%; border-collapse: collapse; }
.table th, .table td { text-align: left; border-bottom: 1px solid var(--border); padding: 12px 10px; vertical-align: top; }
.badge { display: inline-block; padding: 4px 10px; border-radius: 999px; font-size: .85rem; }
.badge.live { background: rgba(34,197,94,.15); color: #86efac; }
pre { white-space: pre-wrap; word-break: break-word; color: var(--muted); }
.empty { color: var(--muted); }
@media (max-width: 900px) { .metrics { grid-template-columns: repeat(2, 1fr); } }
@media (max-width: 640px) { .metrics { grid-template-columns: 1fr; } .shell { padding: 16px; } }
""";

    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Symphony Observability</title>
  <link rel="stylesheet" href="/dashboard.css">
</head>
<body>
  <main class="shell">
    <section class="hero">
      <span class="badge live">Live</span>
      <h1>Symphony Observability</h1>
      <p>ASP.NET Core port scaffold mirroring the Elixir dashboard payload and routes.</p>
    </section>
    <section class="metrics" id="metrics"></section>
    <section class="panel">
      <h2>Rate limits</h2>
      <pre id="rateLimits"></pre>
    </section>
    <section class="panel">
      <h2>Running sessions</h2>
      <div id="running"></div>
    </section>
    <section class="panel">
      <h2>Retry queue</h2>
      <div id="retrying"></div>
    </section>
  </main>
<script>
async function load() {
  const response = await fetch('/api/v1/state');
  const data = await response.json();
  renderMetrics(data);
  document.getElementById('rateLimits').textContent = JSON.stringify(data.rate_limits, null, 2);
  renderRunning(data.running || []);
  renderRetrying(data.retrying || []);
}
function renderMetrics(data) {
  const metrics = [
    ['Running', data.counts?.running ?? 0],
    ['Retrying', data.counts?.retrying ?? 0],
    ['Total tokens', data.codex_totals?.total_tokens ?? 0],
    ['Runtime', data.codex_totals?.seconds_running ?? 0]
  ];
  document.getElementById('metrics').innerHTML = metrics.map(function(item) {
    return '<article class="metric"><div class="label">' + item[0] + '</div><div class="value">' + item[1] + '</div></article>';
  }).join('');
}
function renderRunning(items) {
  if (!items.length) {
    document.getElementById('running').innerHTML = '<p class="empty">No active sessions.</p>';
    return;
  }
  const rows = items.map(function(item) {
    return '<tr>' +
      '<td><strong>' + item.issue_identifier + '</strong><br><a href="/api/v1/' + item.issue_identifier + '">JSON details</a></td>' +
      '<td>' + item.state + '</td>' +
      '<td>' + (item.session_id || 'n/a') + '</td>' +
      '<td>' + item.turn_count + '</td>' +
      '<td>' + (item.last_message || item.last_event || 'n/a') + '</td>' +
      '<td>' + (item.tokens?.total_tokens || 0) + '</td>' +
      '</tr>';
  }).join('');
  document.getElementById('running').innerHTML = '<table class="table"><thead><tr><th>Issue</th><th>State</th><th>Session</th><th>Turns</th><th>Codex update</th><th>Tokens</th></tr></thead><tbody>' + rows + '</tbody></table>';
}
function renderRetrying(items) {
  if (!items.length) {
    document.getElementById('retrying').innerHTML = '<p class="empty">No issues are currently backing off.</p>';
    return;
  }
  const rows = items.map(function(item) {
    return '<tr>' +
      '<td><strong>' + item.issue_identifier + '</strong><br><a href="/api/v1/' + item.issue_identifier + '">JSON details</a></td>' +
      '<td>' + item.attempt + '</td>' +
      '<td>' + item.due_at + '</td>' +
      '<td>' + item.error + '</td>' +
      '</tr>';
  }).join('');
  document.getElementById('retrying').innerHTML = '<table class="table"><thead><tr><th>Issue</th><th>Attempt</th><th>Due at</th><th>Error</th></tr></thead><tbody>' + rows + '</tbody></table>';
}
load();
setInterval(load, 1000);
</script>
</body>
</html>
""";
}
