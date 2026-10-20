(() => {
  const total = document.getElementById('total-count');
  const traditional = document.getElementById('traditional-count');
  const seamless = document.getElementById('seamless-count');
  const note = document.getElementById('status-note');

  async function refreshStatus() {
    try {
      const response = await fetch('/v1/status', { cache: 'no-store' });
      if (!response.ok) throw new Error('status unavailable');
      const data = await response.json();
      if (!data.ok) throw new Error('status not ready');
      const count = (value) => Math.max(0, Number(value) || 0).toLocaleString('zh-CN');
      total.textContent = count(data.online_players);
      traditional.textContent = count(data.traditional_online_players);
      seamless.textContent = count(data.seamless_online_players);
      note.textContent = '数据来自公开联机状态 · 每 60 秒更新';
    } catch {
      note.textContent = '状态暂不可用；不影响查看指南';
    }
  }

  refreshStatus();
  setInterval(refreshStatus, 60_000);
})();
