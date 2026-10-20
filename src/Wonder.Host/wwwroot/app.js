const $ = s => document.querySelector(s);
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
}[c]));
const state = {view: 'bots', bot: null, tab: 'settings', me: null};
let toastTimer;

function toast(text) {
    $('#toast').textContent = text;
    $('#toast').style.display = 'block';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => $('#toast').style.display = 'none', 4500);
}

async function api(path, method = 'GET', body) {
    const response = await fetch('/api/v1' + path, {
        method,
        headers: {'Content-Type': 'application/json', 'X-Wonder': '1'},
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (response.status === 401) {
        showLogin();
        throw Error('请先登录。');
    }
    const data = await response.text();
    let parsed;
    try {
        parsed = data ? JSON.parse(data) : null;
    } catch {
        parsed = null;
    }
    if (!response.ok) throw Error(parsed?.error || parsed?.title || ('请求未完成（' + response.status + '）'));
    return parsed;
}

function showLogin() {
    $('#login').hidden = false;
    $('#shell').hidden = true;
}

function showShell() {
    $('#login').hidden = true;
    $('#shell').hidden = false;
    $('#account').textContent = state.me.email;
}

function formData(form) {
    return Object.fromEntries(new FormData(form));
}

function check(form, name) {
    return form.elements[name].checked;
}

function field(label, name, value, type = 'text', extra = '') {
    return '<label>' + label + '<input name="' + name + '" type="' + type + '" value="' + esc(value) + '" ' + extra + '></label>';
}

function checkbox(label, name, value) {
    return '<label class="check"><input type="checkbox" name="' + name + '" ' + (value ? 'checked' : '') + '>' + label + '</label>';
}

const empty = text => '<div class="empty">' + text + '</div>';
$('#auth-form').onsubmit = async e => {
    e.preventDefault();
    try {
        await api('/auth/login', 'POST', formData(e.target));
        state.me = await api('/me');
        showShell();
        await render();
    } catch (error) {
        $('#auth-error').textContent = error.message;
    }
};
$('#register').onclick = async () => {
    try {
        await api('/auth/register', 'POST', formData($('#auth-form')));
        toast('账号已创建，可以登录了。');
    } catch (error) {
        $('#auth-error').textContent = error.message;
    }
};
$('#logout').onclick = async () => {
    await api('/auth/logout', 'POST');
    state.bot = null;
    showLogin();
};
$('#refresh').onclick = () => safe(render());
$('.brand').onclick = e => {
    e.preventDefault();
    state.bot = null;
    state.view = 'bots';
    safe(render());
};
document.querySelectorAll('[data-view]').forEach(b => b.onclick = () => {
    state.view = b.dataset.view;
    state.bot = null;
    safe(render());
});
$('#close-create').onclick = () => $('#create-dialog').close();
$('#create-form').onsubmit = async e => {
    e.preventDefault();
    try {
        state.bot = await api('/bots', 'POST', formData(e.target));
        state.tab = 'settings';
        $('#create-dialog').close();
        e.target.reset();
        toast('实例已创建。');
        await render();
    } catch (error) {
        toast(error.message);
    }
};

async function safe(promise) {
    try {
        await promise;
    } catch (error) {
        toast(error.message);
    }
}

async function render() {
    document.querySelectorAll('[data-view]').forEach(b => b.classList.toggle('active', b.dataset.view === state.view));
    $('#page-title').textContent = state.view === 'memories' ? '我的记忆' : state.bot?.name || 'Bot 工作台';
    $('#page-kicker').textContent = state.view === 'memories' ? 'YOUR MEMORY, YOUR CHOICE' : state.bot ? 'INSTANCE STUDIO' : 'YOUR INSTANCES';
    if (state.view === 'memories') return renderMemories();
    if (!state.bot) return renderBots();
    state.bot = await api('/bots/' + state.bot.id);
    $('#page-title').textContent = state.bot.name;
    const tabs = [['settings', '基本设置'], ['identity', '角色身份'], ['binding', 'QQ 连接'], ['models', '模型路由'], ['groups', '群聊与共享空间'], ['status', '状态与任务']];
    $('#content').innerHTML = '<button class="text-button" id="back">← 所有实例</button><div class="tabs">' + tabs.map(([id, name]) => '<button data-tab="' + id + '" class="' + (state.tab === id ? 'active' : '') + '">' + name + '</button>').join('') + '</div><div id="panel"></div>';
    $('#back').onclick = () => {
        state.bot = null;
        safe(render());
    };
    document.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => {
        state.tab = b.dataset.tab;
        safe(render());
    });
    await ({
        settings: renderSettings,
        identity: renderIdentity,
        binding: renderBinding,
        models: renderModels,
        groups: renderGroups,
        status: renderStatus
    }[state.tab])();
}

async function renderBots() {
    const bots = await api('/bots');
    $('#content').innerHTML = '<div class="toolbar"><p class="muted">' + bots.length + ' 个实例 · 各自拥有独立的身份、心境与记忆</p><button id="new-bot">＋ 创建 Bot</button></div>' + (bots.length ? '<div class="grid">' + bots.map(b => '<article class="card bot-card" tabindex="0" role="button" data-bot="' + b.id + '"><div class="bot-icon">w</div><h3>' + esc(b.name) + '</h3><p class="muted">配置版本 ' + b.settingsRevision + '</p><span class="badge ' + (!b.enabled ? 'off' : '') + '">' + (b.enabled ? '已启用' : '已停用') + '</span></article>').join('') + '</div>' : empty('<h2>从第一个实例开始</h2><p>为它取一个名字，写下最初的身份。</p>'));
    $('#new-bot').onclick = () => $('#create-dialog').showModal();
    document.querySelectorAll('[data-bot]').forEach(b => {
        const open = () => {
            state.bot = bots.find(x => x.id === b.dataset.bot);
            state.tab = 'settings';
            safe(render());
        };
        b.onclick = open;
        b.onkeydown = e => {
            if (e.key === 'Enter') open();
        };
    });
}

function renderSettings() {
    const b = state.bot;
    $('#panel').innerHTML = '<form class="card" id="settings-form"><h3>基本设置</h3><div class="form-grid">' + field('实例名称', 'name', b.name, 'text', 'required maxlength="100"') + field('消息保留天数', 'messageRetentionDays', b.messageRetentionDays, 'number', 'min="1" max="3650"') + field('私聊空闲超时 / 分钟', 'privateIdleMinutes', b.privateIdleMinutes, 'number', 'min="1" max="1440"') + field('群聊空闲超时 / 分钟', 'groupIdleMinutes', b.groupIdleMinutes, 'number', 'min="1" max="1440"') + field('每日 Token 预算', 'dailyTokenLimit', b.dailyTokenLimit, 'number', 'min="1"') + field('每月 Token 预算', 'monthlyTokenLimit', b.monthlyTokenLimit, 'number', 'min="1"') + '</div>' + checkbox('启用此实例', 'enabled', b.enabled) + checkbox('保存原始消息（关闭后暂停回复）', 'persistMessages', b.persistMessages) + '<p class="muted">保留期调整用于之后收到的消息。已保存消息仍按原到期日清理。</p><button>保存设置</button></form>';
    $('#settings-form').onsubmit = e => {
        e.preventDefault();
        const data = formData(e.target);
        for (const k of ['messageRetentionDays', 'privateIdleMinutes', 'groupIdleMinutes', 'dailyTokenLimit', 'monthlyTokenLimit']) data[k] = Number(data[k]);
        data.enabled = check(e.target, 'enabled');
        data.persistMessages = check(e.target, 'persistMessages');
        safe(api('/bots/' + b.id, 'PATCH', data).then(() => {
            toast('设置已保存。');
            return render();
        }));
    };
}

async function renderIdentity() {
    const versions = await api('/bots/' + state.bot.id + '/identity');
    $('#panel').innerHTML = '<form class="card" id="identity-form"><h3>稳定的角色身份</h3><p class="muted">由你明确发布。对话和自动反思不会修改这里的内容。</p><label>当前身份 · 版本 ' + versions[0].revision + '<textarea name="content" rows="9" maxlength="4000" required>' + esc(versions[0].content) + '</textarea></label><button>发布新版本</button></form><h3 class="section-heading">最近版本</h3>' + versions.slice(1).map(v => '<details class="card memory"><summary>版本 ' + v.revision + ' · ' + new Date(v.publishedAt).toLocaleString() + '</summary><pre>' + esc(v.content) + '</pre></details>').join('');
    $('#identity-form').onsubmit = e => {
        e.preventDefault();
        safe(api('/bots/' + state.bot.id + '/identity', 'PUT', formData(e.target)).then(() => {
            toast('新身份已发布。');
            return render();
        }));
    };
}

async function renderBinding() {
    const b = await api('/bots/' + state.bot.id + '/binding');
    $('#panel').innerHTML = '<form class="card" id="binding-form"><h3>连接 NapCat</h3><p class="muted">在 NapCat 中启用 OneBot 11 正向 WebSocket，使用数组格式的消息段。</p>' + field('QQ 账号', 'platformAccountId', b?.platformAccountId || '', 'text', 'required inputmode="numeric"') + field('WebSocket 地址', 'webSocketUrl', b?.webSocketUrl || 'ws://127.0.0.1:3001', 'text', 'required') + field(b?.hasAccessToken ? '访问令牌（留空保留）' : '访问令牌', 'accessToken', '', 'password', 'autocomplete="new-password"') + checkbox('启用连接', 'enabled', b?.enabled ?? true) + '<button>保存连接</button></form>';
    $('#binding-form').onsubmit = e => {
        e.preventDefault();
        const data = formData(e.target);
        data.enabled = check(e.target, 'enabled');
        safe(api('/bots/' + state.bot.id + '/binding', 'PUT', data).then(() => toast('连接已保存，稍后自动连接。')));
    };
}

async function renderModels() {
    const routes = await api('/bots/' + state.bot.id + '/model-routes');
    const tasks = {
        Chat: '聊天回复',
        LightReflection: '轻量反思',
        StrongReflection: '复杂反思',
        DailyNarrative: '每日叙事',
        RollingSummary: '滚动摘要',
        Embedding: '记忆向量'
    };
    const generationTasks = Object.entries(tasks).filter(([id]) => id !== 'Embedding');
    const dropdown = (label, name, values, current) => '<label>' + label + '<select name="' + name + '">' + values.map(([value, text]) => '<option value="' + value + '" ' + (value === current ? 'selected' : '') + '>' + text + '</option>').join('') + '</select></label>';

    function routeFields(r = {}, embedding = false, batch = false) {
        return '<div class="form-grid">' +
            field('接口基础地址', 'endpoint', r.endpoint || '', 'url', 'required placeholder="https://provider.example/v1/"') +
            field('模型名称', 'model', r.model || '', 'text', 'required') +
            field(batch ? 'API Key（已配置任务留空保留）' : r.hasApiKey ? 'API Key（留空保留）' : 'API Key', 'apiKey', '', 'password', 'autocomplete="new-password"') +
            field('上下文窗口', 'contextWindow', r.contextWindow ?? 8192, 'number', 'min="2048" max="2000000"') +
            field('最大输出 Token', 'maxOutputTokens', r.maxOutputTokens ?? 1024, 'number', 'min="16" max="32768"') +
            (embedding ? field('Embedding 维度', 'embeddingDimensions', r.embeddingDimensions ?? 1536, 'number', 'min="1" max="8192"') : '') +
            field('输入价格 / 百万 Token', 'inputCostPerMillion', r.inputCostPerMillion ?? 0, 'number', 'min="0" step="0.001"') +
            field('输出价格 / 百万 Token', 'outputCostPerMillion', r.outputCostPerMillion ?? 0, 'number', 'min="0" step="0.001"') + '</div>' +
            (embedding ? '<p class="muted">请填写提供向量接口的 Embedding 模型。</p>' :
                checkbox('支持图片输入', 'supportsImages', r.supportsImages) +
                checkbox('支持 JSON 输出格式', 'supportsJson', r.supportsJson) +
                dropdown('思考模式', 'thinkingEnabled', [['', '跟随供应商默认'], ['false', '关闭'], ['true', '开启']], r.thinkingEnabled == null ? '' : String(r.thinkingEnabled)) +
                dropdown('思考强度', 'reasoningEffort', [['', '跟随供应商默认'], ['low', 'low'], ['high', 'high'], ['max', 'max']], r.reasoningEffort || '') +
                '<p class="muted">按模型实际能力设置图片、JSON 和思考参数。思考内容不会显示在聊天中；开启时请为思考和最终回答预留足够的输出 Token。</p>');
    }

    function setupThinking(form) {
        const thinking = form.elements.thinkingEnabled;
        const effort = form.elements.reasoningEffort;
        if (!thinking) return;
        const update = () => {
            effort.disabled = thinking.value !== 'true';
            if (effort.disabled) effort.value = '';
        };
        thinking.onchange = update;
        update();
    }

    function readRoute(form) {
        const data = formData(form);
        delete data.taskKinds;
        for (const k of ['contextWindow', 'maxOutputTokens', 'embeddingDimensions', 'inputCostPerMillion', 'outputCostPerMillion']) {
            if (k in data) data[k] = Number(data[k]);
        }
        data.supportsImages = form.elements.supportsImages ? check(form, 'supportsImages') : false;
        data.supportsJson = form.elements.supportsJson ? check(form, 'supportsJson') : false;
        data.thinkingEnabled = data.thinkingEnabled === 'true' ? true : data.thinkingEnabled === 'false' ? false : null;
        data.reasoningEffort = data.thinkingEnabled === true ? data.reasoningEffort || null : null;
        return data;
    }

    $('#panel').innerHTML = '<div class="notice">同一个模型可以承担多项任务，支持兼容的供应商接口。Embedding 请单独配置向量模型。</div>' +
        '<form class="card" id="batch-model-form"><h3>批量应用模型</h3><p class="muted">将下方模型、能力和参数统一覆盖到勾选的生成任务，之后仍可逐项调整。新任务需要填写 API Key；留空会保留各任务已有的密钥。</p>' +
        '<div class="form-grid" role="group" aria-label="批量应用的任务">' + generationTasks.map(([id, name]) => '<label class="check"><input type="checkbox" name="taskKinds" value="' + id + '" checked>' + name + '</label>').join('') + '</div>' +
        routeFields({contextWindow: 32768, maxOutputTokens: 2048}, false, true) + '<button>应用到所选任务</button></form>' +
        '<form class="card memory" id="model-form"><h3>单独调整任务</h3><label>任务<select name="taskKind">' + Object.entries(tasks).map(([id, name]) => '<option value="' + id + '">' + name + '</option>').join('') + '</select></label><div id="model-fields"></div><button>保存此任务</button></form>';
    const batchForm = $('#batch-model-form');
    setupThinking(batchForm);
    batchForm.onsubmit = e => {
        e.preventDefault();
        const selected = Array.from(batchForm.querySelectorAll('[name=taskKinds]:checked'), input => input.value);
        if (!selected.length) return toast('请至少选择一个任务。');
        const shared = readRoute(batchForm);
        if (!shared.apiKey && selected.some(task => !routes.find(r => r.taskKind === task)?.hasApiKey))
            return toast('所选任务中有尚未配置密钥的任务，请填写 API Key。');
        const data = selected.map(taskKind => ({...shared, taskKind}));
        safe(api('/bots/' + state.bot.id + '/model-routes', 'PUT', data).then(() => {
            toast('已将模型应用到 ' + selected.length + ' 项任务。');
            return render();
        }));
    };
    const modelForm = $('#model-form');
    const select = modelForm.elements.taskKind;
    function fields() {
        const route = routes.find(x => x.taskKind === select.value) || {};
        $('#model-fields').innerHTML = routeFields(route, select.value === 'Embedding');
        setupThinking(modelForm);
    }
    select.onchange = fields;
    fields();
    modelForm.onsubmit = e => {
        e.preventDefault();
        safe(api('/bots/' + state.bot.id + '/model-routes', 'PUT', [readRoute(modelForm)]).then(() => {
            toast('任务路由已保存。');
            return render();
        }));
    };
}

async function renderGroups() {
    const base = '/bots/' + state.bot.id;
    const [groups, spaces] = await Promise.all([api(base + '/groups'), api(base + '/context-spaces')]);
    $('#panel').innerHTML = '<div class="notice">把需要互通共享记忆的群加入同一个空间。原始聊天和群内摘要仍各自保留，只有本人明确共享的记忆会跨群使用。</div>' +
        '<form class="card" id="space-form"><h3>创建共享空间</h3><p class="muted">由当前 Bot Owner 创建和管理。创建后，在下方群聊策略中选择空间并保存。</p>' + field('空间名称', 'name', '', 'text', 'required maxlength="100"') + '<button>创建空间</button></form>' +
        '<div class="memory">' + spaces.map(s => '<div class="card memory"><strong>' + esc(s.name) + '</strong> <button class="secondary" data-rename-space="' + s.id + '">改名</button> <button class="danger" data-delete-space="' + s.id + '">删除空间</button></div>').join('') + '</div>' +
        '<form class="card memory" id="group-form"><h3>群聊策略</h3>' + field('群号', 'groupId', '', 'text', 'required inputmode="numeric"') +
        '<label>共享空间<select name="contextSpaceId"><option value="">不加入共享空间</option>' + spaces.map(s => '<option value="' + s.id + '">' + esc(s.name) + '</option>').join('') + '</select></label>' +
        field('命令前缀', 'commandPrefix', '/wonder') + checkbox('被 @ 时回复', 'respondToMention', true) + checkbox('被引用时回复', 'respondToReply', true) +
        checkbox('保存群消息', 'persistMessages', true) + checkbox('对有授权的发言进行反思', 'allowReflection', true) + '<button>保存群策略</button></form>' +
        '<h3 class="section-heading">已配置的群</h3>' + groups.map(g => '<button class="secondary" data-group="' + esc(g.groupId) + '">' + esc(g.groupId) + ' · ' + esc(spaces.find(s => s.id === g.contextSpaceId)?.name || '独立') + '</button>').join(' ');
    $('#space-form').onsubmit = e => {
        e.preventDefault();
        safe(api(base + '/context-spaces', 'POST', formData(e.target)).then(() => {
            toast('共享空间已创建，可以在群聊策略中选择。');
            return render();
        }));
    };
    document.querySelectorAll('[data-rename-space]').forEach(b => b.onclick = () => {
        const space = spaces.find(s => s.id === b.dataset.renameSpace);
        const name = prompt('共享空间名称', space.name);
        if (name?.trim()) safe(api(base + '/context-spaces/' + space.id, 'PUT', {name}).then(render));
    });
    document.querySelectorAll('[data-delete-space]').forEach(b => b.onclick = () => {
        if (confirm('删除此空间，并让关联群退出共享？')) safe(api(base + '/context-spaces/' + b.dataset.deleteSpace, 'DELETE').then(render));
    });
    document.querySelectorAll('[data-group]').forEach(b => b.onclick = () => {
        const g = groups.find(x => x.groupId === b.dataset.group);
        for (const [k, v] of Object.entries(g)) {
            const f = $('#group-form').elements[k];
            if (f) {
                if (f.type === 'checkbox') f.checked = v; else f.value = v ?? '';
            }
        }
    });
    $('#group-form').onsubmit = e => {
        e.preventDefault();
        const data = formData(e.target);
        const group = data.groupId;
        delete data.groupId;
        data.contextSpaceId = data.contextSpaceId || null;
        for (const k of ['respondToMention', 'respondToReply', 'persistMessages', 'allowReflection']) data[k] = check(e.target, k);
        safe(api(base + '/groups/' + encodeURIComponent(group) + '/policy', 'PUT', data).then(() => {
            toast('群策略已保存。');
            return render();
        }));
    };
}

async function renderStatus() {
    const base = '/bots/' + state.bot.id;
    const [emotion, usage, jobs, deliveries] = await Promise.all([api(base + '/emotion'), api(base + '/usage'), api(base + '/jobs'), api(base + '/deliveries')]);
    const m = emotion.state;
    $('#panel').innerHTML = '<p class="muted">以下数值仅用于诊断角色的模拟表达状态。</p><div class="grid">' + [['愉悦度', m.valence], ['唤醒度', m.arousal], ['能量', m.energy]].map(([name, value]) => '<div class="card"><p class="eyebrow">' + name + '</p><div class="metric">' + Number(value).toFixed(2) + '</div><span class="muted">状态版本 ' + m.revision + '</span></div>').join('') + '</div>' + (emotion.narrative ? '<div class="card memory"><h3>今日叙事</h3><p>' + esc(emotion.narrative.text) + '</p></div>' : '') + '<div class="card memory"><h3>最近 30 天用量</h3>' + table(['任务', '输入', '输出', '费用估算', '请求'], usage.map(u => [esc(u.taskKind), u.inputTokens, u.outputTokens, Number(u.cost).toFixed(4), u.requests])) + '</div><div class="card memory"><h3>发送结果待确认</h3><p class="muted">请在 QQ 中核对。处理分段后，到下方任务列表重试对应发送任务。</p>' + table(['分段', '状态', '处理'], deliveries.map(d => [esc(d.id.slice(0, 8) + ' / ' + d.sequence), esc(d.status), '<button data-confirm="' + d.id + '">确认已发出</button> <button class="secondary" data-resend="' + d.id + '">允许重发（可能重复）</button>'])) + '</div><div class="card memory"><h3>最近任务</h3>' + table(['任务', '状态', '尝试', '诊断', '操作'], jobs.map(j => [esc(j.type), esc(j.status), j.attempts, esc(j.errorCode || '—'), j.status === 'DeadLetter' ? '<button data-retry="' + j.id + '">重试</button>' : '—'])) + '</div>';
    document.querySelectorAll('[data-retry]').forEach(b => b.onclick = () => safe(api(base + '/jobs/' + b.dataset.retry + '/retry', 'POST').then(render)));
    for (const [attr, action] of [['confirm', 'confirm-sent'], ['resend', 'retry']]) document.querySelectorAll('[data-' + attr + ']').forEach(b => b.onclick = () => safe(api(base + '/deliveries/' + b.dataset[attr] + '/resolve', 'POST', {action}).then(render)));
}

function table(headers, rows) {
    if (!rows.length) return '<p class="muted">暂无记录。</p>';
    return '<div class="table-wrap"><table><thead><tr>' + headers.map(h => '<th>' + h + '</th>').join('') + '</tr></thead><tbody>' + rows.map(r => '<tr>' + r.map(c => '<td>' + c + '</td>').join('') + '</tr>').join('') + '</tbody></table></div>';
}

async function renderMemories() {
    state.me = await api('/me');
    if (!state.me.logicalUserId) {
        $('#content').innerHTML = '<div class="card"><h2>先关联你的 QQ</h2><p class="muted">通过已连接 Bot 的私聊验证身份，之后就能查看和管理自己的记忆。</p><button id="bind-qq">生成关联口令</button><div id="bind-info"></div></div>';
        $('#bind-qq').onclick = () => safe(api('/me/qq-binding-challenge', 'POST').then(r => {
            $('#bind-info').innerHTML = '<p class="notice">请在 5 分钟内私聊 Bot，发送：<br><strong>' + esc('/bind ' + r.code) + '</strong><br>发送后点击右上角刷新。</p>';
        }));
        return;
    }
    const [settings, memories] = await Promise.all([api('/me/memory-settings'), api('/me/memories')]);
    const labels = {Private: '仅私聊', GroupUser: '指定群内', GroupShared: '群公共', GlobalPublic: '公开', UserShared: '我的跨群记忆', SpaceShared: '共享空间'};
    $('#content').innerHTML = '<form class="card" id="memory-settings"><h3>我的称呼与记忆偏好</h3>' +
        field('希望 Bot 如何称呼我', 'preferredName', settings.preferredName || '', 'text', 'maxlength="100"') +
        '<p class="muted">你主动设置的称呼会随当前发送者身份提供给模型，在各群和私聊中使用。留空时使用自然称呼，不把 Bot 的名字当成你的名字。</p>' +
        checkbox('允许保存非敏感的长期事实与交流偏好', 'allowMemory', settings.allowMemory) +
        checkbox('允许逐条共享我的记忆到其他群', 'allowCrossGroupMemory', settings.allowCrossGroupMemory) +
        '<p class="muted">开启开关不会自动共享任何内容。关闭记忆或跨群开关会撤回已有共享；再次开启后需要重新选择共享。</p><button>保存称呼与偏好</button></form>' +
        '<div class="toolbar memory"><h3>我的记忆</h3><span class="muted">最近 ' + memories.length + ' 条</span></div>' +
        (memories.length ? memories.map(m => '<article class="card memory"><span class="badge">' + esc(labels[m.scope] || m.scope) + '</span><p class="memory-body">' + esc(m.text) + '</p><p class="muted">' + esc(m.factKey) + ' · ' + esc(m.status) + '</p>' +
            (m.sharing || []).map(s => '<p class="muted">' + (s.available === false ? '暂不可用（群/空间已变化）：' : '已共享：') + esc(labels[s.scope]) + ' <button class="secondary" data-unshare="' + s.id + '" data-memory="' + m.id + '">撤回共享</button></p>').join('') +
            '<button class="secondary" data-share="' + m.id + '">共享此条</button> <button class="secondary" data-edit="' + m.id + '">更正</button> <button class="danger" data-delete="' + m.id + '">删除（含共享）</button><div id="sharing-' + m.id + '"></div></article>').join('') : empty('还没有保存的记忆。'));
    const settingsForm = $('#memory-settings');
    const updateCrossGroup = () => {
        settingsForm.elements.allowCrossGroupMemory.disabled = !settingsForm.elements.allowMemory.checked;
        if (!settingsForm.elements.allowMemory.checked) settingsForm.elements.allowCrossGroupMemory.checked = false;
    };
    settingsForm.elements.allowMemory.onchange = updateCrossGroup;
    updateCrossGroup();
    settingsForm.onsubmit = e => {
        e.preventDefault();
        safe(api('/me/memory-settings', 'PUT', {preferredName: formData(settingsForm).preferredName,
            allowMemory: check(settingsForm, 'allowMemory'), allowCrossGroupMemory: check(settingsForm, 'allowCrossGroupMemory')}).then(() => {
            toast('称呼与偏好已保存。');
            return render();
        }));
    };
    document.querySelectorAll('[data-share]').forEach(b => b.onclick = () => safe((async () => {
        if (!settings.allowMemory || !settings.allowCrossGroupMemory) return toast('请先保存并开启记忆和跨群共享开关。');
        const m = memories.find(x => x.id === b.dataset.share);
        const options = await api('/me/memories/' + m.id + '/sharing-options');
        const panel = $('#sharing-' + m.id);
        panel.innerHTML = '<p class="muted">请选择此条记忆的使用范围：</p><button class="secondary" data-scope="UserShared">仅用于我在其他群和私聊中的对话</button>' +
            (options.space ? ' <button class="secondary" data-scope="SpaceShared">共享到空间：' + esc(options.space.name) + '（空间内其他成员也可使用）</button>' : '<p class="muted">空间共享需要来源群已加入一个共享空间。</p>');
        panel.querySelectorAll('[data-scope]').forEach(choice => choice.onclick = () => safe(api('/me/memories/' + m.id + '/sharing', 'POST',
            {scope: choice.dataset.scope, contextSpaceId: choice.dataset.scope === 'SpaceShared' ? options.space.id : null}).then(render)));
    })()));
    document.querySelectorAll('[data-unshare]').forEach(b => b.onclick = () => safe(api('/me/memories/' + b.dataset.memory + '/sharing/' + b.dataset.unshare, 'DELETE').then(render)));
    document.querySelectorAll('[data-delete]').forEach(b => b.onclick = () => safe(api('/me/memories/' + b.dataset.delete, 'DELETE').then(() => {
        toast('记忆及关联共享已删除。');
        return render();
    })));
    document.querySelectorAll('[data-edit]').forEach(b => b.onclick = () => {
        const m = memories.find(x => x.id === b.dataset.edit);
        const text = prompt('更正这条记忆（已有共享会撤回，更正后可重新共享）', m.text);
        if (text?.trim()) safe(api('/me/memories/' + m.id, 'PATCH', {text}).then(() => {
            toast('记忆已更正，原共享已撤回。');
            return render();
        }));
    });
}

try {
    state.me = await api('/me');
    showShell();
    await render();
} catch {
    showLogin();
}
