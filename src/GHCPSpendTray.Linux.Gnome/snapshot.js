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

function band(value) {
    return ['Under', 'Near', 'Over'].includes(value);
}

function usagePosition(value) {
    return value == null || (typeof value === 'object' &&
        typeof value.fraction === 'number' && value.fraction >= 0 && value.fraction <= 1 &&
        percent(value.percent) && band(value.band));
}

function forecast(value) {
    return value == null || (typeof value === 'object' && percent(value.percent) && band(value.band) &&
        typeof value.actualFraction === 'number' && value.actualFraction >= 0 && value.actualFraction <= 1 &&
        typeof value.forecastFraction === 'number' && value.forecastFraction >= value.actualFraction &&
        value.forecastFraction <= 1);
}

export function parseSnapshot(json) {
    requireValue(typeof json === 'string' && json.length <= 1024 * 1024, 'payload size');
    const value = JSON.parse(json);
    requireValue(value !== null && typeof value === 'object', 'object');
    requireValue(value.version === 4 && typeof value.demo === 'boolean', 'unsupported version');
    requireValue(Number.isSafeInteger(value.revision) && value.revision >= 0, 'revision');
    requireValue(['Pie', 'Percentage'].includes(value.style), 'style');
    requireValue(percent(value.percent) && text(value.indicator) && text(value.consumption) &&
        text(value.status) && timestamp(value.updatedAt), 'summary');
    requireValue(Array.isArray(value.accounts) && value.accounts.length <= 100, 'accounts');
    const keys = new Set();
    for (const account of value.accounts) {
        requireValue(account && ['key', 'name', 'login', 'host', 'consumption', 'allocation', 'freshness']
            .every(key => text(account[key])) && percent(account.percent) && timestamp(account.updatedAt), 'account');
        requireValue(text(account.displayName) && text(account.thresholds) &&
            percent(account.spendIncrementUsd) && typeof account.showPeriodEstimate === 'boolean' &&
            (account.clientId === null || text(account.clientId)) &&
            (account.message === null || text(account.message)), 'account settings');
        requireValue(usagePosition(account.actualUsage) && forecast(account.forecast) &&
            (account.customBudgetUsd === undefined || percent(account.customBudgetUsd)) &&
            (account.trackingTarget === undefined || text(account.trackingTarget)) &&
            (account.targetLabel === undefined || ['API limit', 'custom budget'].includes(account.targetLabel)),
        'account forecast');
        requireValue(account.key.length > 0 && !keys.has(account.key), 'duplicate or empty account key');
        keys.add(account.key);
    }
    requireValue(value.settings && Number.isInteger(value.settings.pollMinutes) &&
        ['Pie', 'Percentage'].includes(value.settings.trayStyle) &&
        ['RollUp', 'PerAccount'].includes(value.settings.trayMode), 'global settings');
    validateTray(value);
    return value;
}

function validateTray(value) {
    requireValue(value.tray && value.tray.rollUp && Array.isArray(value.icons) &&
        value.icons.length > 0 && value.icons.length <= 100, 'tray');
    for (const icon of value.icons)
        requireValue(icon && text(icon.name) && typeof icon.tooltip === 'string' &&
            typeof icon.imageUri === 'string' && icon.imageUri.startsWith('data:image/png;base64,') &&
            icon.imageUri.length < 16384, 'tray icon');
}

export function parsePreview(json) {
    requireValue(typeof json === 'string' && json.length <= 1024 * 1024, 'preview size');
    const value = JSON.parse(json);
    validateTray(value);
    return value;
}

export function amount(value) {
    if (value === null || value === undefined) return 'Unavailable';
    const [whole, cents] = Number(value).toFixed(2).split('.');
    const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    return '$' + grouped + (cents === '00' ? '' : '.' + cents);
}

export function estimateText(estimate) {
    if (!estimate) return '';
    if (estimate.unavailableReason) return 'Estimate unavailable: ' + estimate.unavailableReason;
    return `Estimated at reset: ~${amount(estimate.estimatedConsumptionUsd)}` +
        (estimate.overAllocationUsd > 0 ? ` (~${amount(estimate.overAllocationUsd)} over allocation)` : '') +
        (estimate.isEarly ? ' - Early estimate' : '') +
        '\nAverage pace so far this UTC calendar month; assumes the same pace continues. Not an invoice.';
}

export function forecastText(account) {
    if (!account?.forecast || !account.periodEstimate) return '';
    const status = account.forecast.band === 'Near' ? 'Near target' :
        account.forecast.band === 'Over' ? 'At or over target' : 'Under target';
    return `Estimated at reset: ~${amount(account.periodEstimate.estimatedConsumptionUsd)} ` +
        `(${formatPercent(account.forecast.percent)} of ${account.targetLabel || 'target'}). ${status}.` +
        (account.periodEstimate.isEarly ? ' Early estimate.' : '') +
        ' Average pace so far this UTC calendar month; assumes the same pace continues. Not an invoice.';
}

export function diagnosticsText(account) {
    const d = account.details;
    if (!d) return account.message || '';
    const time = value => value ? new Date(value).toLocaleString() : 'Unavailable';
    let text = `Credits used (observed): ${d.creditsUsed ?? 'Unavailable'}\n` +
        `Consumption (observed): ${amount(d.observedConsumptionUsd)}\n` +
        `Allocation (observed): ${d.unlimited ? 'Unlimited' : amount(d.observedAllocationUsd)}\n` +
        `Percent (observed): ${formatPercent(d.observedPercentConsumed)}\n` +
        `Current period: ${d.isCurrentPeriod ? 'Yes' : 'No'}\n` +
        `Source timestamp: ${time(d.sourceTimestampUtc)}\nResets: ${time(d.resetAtUtc)}\n` +
        `Next refresh: ${time(d.nextRefreshUtc)}\nPeriod: ${d.periodId ?? 'Unavailable'}`;
    if (account.periodEstimate) {
        const e = account.periodEstimate;
        text += `\n${estimateText(e)}\nEstimated daily average: ~${amount(e.averageDailyConsumptionUsd)}` +
            `\nEstimate starts: ${time(e.periodStartUtc)}\nEstimate resets: ${time(e.resetAtUtc)}` +
            `\nEstimate observed at: ${time(e.observedAtUtc)}`;
    }
    if (d.message) text += '\n' + d.message;
    return text;
}

export function parseSignIn(json) {
    requireValue(typeof json === 'string' && json.length <= 16384, 'sign-in payload size');
    const value = JSON.parse(json);
    requireValue(value && ['idle', 'starting', 'waiting', 'saving', 'complete', 'cancelled', 'error']
        .includes(value.phase), 'sign-in phase');
    requireValue(value.message === null || text(value.message), 'sign-in message');
    if (value.phase === 'waiting') {
        requireValue(text(value.code) && value.code.length > 0 &&
            text(value.verificationUri) && /^https:\/\/[a-z0-9.-]+(?::[0-9]+)?\//i.test(value.verificationUri) &&
            typeof value.expires === 'string' && timestamp(value.expires), 'device prompt');
    } else {
        requireValue(value.code === null && value.verificationUri === null, 'unexpected device prompt');
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
