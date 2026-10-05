function requireValue(condition, description) {
    if (!condition)
        throw new Error(`Invalid helper snapshot: ${description}`);
}

function text(value) {
    return typeof value === 'string' && value.length <= 1024;
}

function percent(value) {
    return value === null || (typeof value === 'number' && Number.isFinite(value) && value >= 0);
}

function timestamp(value) {
    return value === null || (text(value) && Number.isFinite(Date.parse(value)));
}

export function parseSnapshot(json) {
    requireValue(typeof json === 'string' && json.length <= 256 * 1024, 'payload size');
    const value = JSON.parse(json);
    requireValue(value !== null && typeof value === 'object', 'object');
    requireValue(value.version === 2 && value.demo === true, 'unsupported version or non-demo data');
    requireValue(Number.isSafeInteger(value.revision) && value.revision >= 0, 'revision');
    requireValue(['Pie', 'Percentage'].includes(value.style), 'style');
    requireValue(percent(value.percent) && text(value.indicator) && text(value.consumption) &&
        text(value.status) && timestamp(value.updatedAt), 'summary');
    requireValue(Array.isArray(value.accounts) && value.accounts.length <= 100, 'accounts');
    const keys = new Set();
    for (const account of value.accounts) {
        requireValue(account && ['key', 'name', 'login', 'host', 'consumption', 'allocation', 'freshness']
            .every(key => text(account[key])) && percent(account.percent) && timestamp(account.updatedAt), 'account');
        requireValue(account.key.length > 0 && !keys.has(account.key), 'duplicate or empty account key');
        keys.add(account.key);
    }
    return value;
}

export function formatPercent(value) {
    return value === null ? 'Unavailable' : `${Math.round(value * 100) / 100}%`;
}

export function updatedText(value) {
    return value === null ? 'No observations yet' :
        `Oldest update ${new Date(value).toLocaleTimeString([], {hour: '2-digit', minute: '2-digit'})}`;
}
