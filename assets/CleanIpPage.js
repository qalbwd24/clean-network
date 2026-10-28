(() => {
  // Read only the risk items already rendered by CleanIP. Connection/environment
  // checks belong to the browser and are deliberately excluded from IP quality.
  const summary = document.querySelector('.risk-summary');
  if (!summary) return { checks: [] };
  const checks = Array.from(summary.querySelectorAll('.risk-matrix-row:not(.risk-matrix-row--connection) .risk-chip, .risk-matrix-row:not(.risk-matrix-row--connection) .risk-chip-identity')).map(item => ({
    label: (item.querySelector('.risk-chip__label')?.textContent || item.textContent || '').trim().slice(0,100),
    status: item.classList.contains('risk-chip--hit') || item.classList.contains('risk-chip-identity') ? 'hit' : item.classList.contains('risk-chip--clear') ? 'clear' : 'unknown',
    verdict: (item.getAttribute('title') || item.getAttribute('aria-label') || '').split('\n')[0].slice(0,300)
  }));
  const counts = Array.from(summary.querySelectorAll('.risk-summary-count b')).map(item => Number(item.textContent.trim()));
  const expected = counts.length === 3 && counts.every(Number.isFinite) ? counts[1] + counts[2] : checks.length;
  if (expected > checks.length && expected < 100) {
    while (checks.length < expected) checks.push({label:'尚未显示的检查项',status:'unknown'});
  }
  return { checks };
})()
