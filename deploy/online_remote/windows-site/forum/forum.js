(() => {
  const api = '/windows/api';
  const $ = (id) => document.getElementById(id);
  const list = $('thread-list');
  const detail = $('thread-detail');
  const composer = $('composer');
  const moderation = $('moderation');
  const notice = $('forum-notice');
  const dialog = $('auth-dialog');
  let user = null;
  let authMode = 'login';
  let category = '';
  let showingMine = false;
  let currentThread = null;

  const node = (tag, className, value) => {
    const item = document.createElement(tag);
    if (className) item.className = className;
    if (value !== undefined) item.textContent = value;
    return item;
  };
  const time = (seconds) => new Date(seconds * 1000).toLocaleString('zh-CN', { hour12: false });
  const say = (message, error = false) => {
    notice.textContent = message || '';
    notice.style.color = error ? '#ed8e88' : '';
  };

  async function request(path, options = {}) {
    const response = await fetch(api + path, {
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', ...(options.body && user ? { 'X-CSRF-Token': user.csrf } : {}) },
      ...options,
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || '请求失败，请稍后重试');
    return result;
  }

  async function refreshAccount() {
    const result = await request('/me');
    user = result.user;
    $('account-button').textContent = user ? user.nickname + ' · 退出' : '登录 / 注册';
    $('my-threads-button').hidden = !user;
    if (user?.is_admin) {
      if (!$('admin-button')) {
        const button = node('button', 'text-link', '审核内容');
        button.id = 'admin-button';
        button.type = 'button';
        button.addEventListener('click', loadModeration);
        $('my-threads-button').after(button);
      }
    } else $('admin-button')?.remove();
  }

  function resetViews(view) {
    list.hidden = view !== 'list';
    detail.hidden = view !== 'detail';
    composer.hidden = view !== 'composer';
    moderation.hidden = view !== 'moderation';
    document.querySelector('.forum-filters').hidden = view !== 'list';
    $('search-form').hidden = view !== 'list';
    $('forum-title').textContent = view === 'list' ? (showingMine ? '我的帖子' : '所有讨论') :
      view === 'detail' ? '讨论详情' : view === 'composer' ? '发起讨论' : '待审核内容';
  }

  function statusPill(status) {
    if (status === 'visible') return null;
    return node('span', 'status-pill ' + status, status === 'pending' ? '待审核' : '未公开');
  }

  async function loadThreads() {
    resetViews('list');
    list.replaceChildren(node('p', 'empty-state', '正在读取讨论…'));
    try {
      const q = $('search-box').value.trim();
      const path = showingMine ? '/mine' : '/threads?' + new URLSearchParams({ category, q });
      const { threads } = await request(path);
      list.replaceChildren();
      if (!threads.length) {
        list.append(node('p', 'empty-state', showingMine ? '你还没有发起讨论。' : '这里还没有讨论，欢迎发起第一篇。'));
        return;
      }
      for (const thread of threads) {
        const card = node('article', 'thread-card');
        card.tabIndex = 0;
        card.setAttribute('role', 'link');
        const meta = node('div', 'thread-meta');
        meta.append(node('span', 'thread-category', thread.category), node('span', '', thread.nickname),
          node('time', '', time(thread.created_at)));
        const pill = statusPill(thread.status);
        if (pill) meta.append(pill);
        card.append(meta, node('h3', '', thread.title), node('p', '', thread.body.replace(/\s+/g, ' ').slice(0, 130)));
        const footer = node('div', 'thread-meta');
        footer.append(node('span', '', thread.reply_count + ' 条回复'));
        card.append(footer);
        card.addEventListener('click', () => { location.hash = 'thread/' + thread.id; });
        card.addEventListener('keydown', (event) => {
          if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); location.hash = 'thread/' + thread.id; }
        });
        list.append(card);
      }
    } catch (error) { list.replaceChildren(); say(error.message, true); }
  }

  function imageList(ids) {
    const wrap = node('div', 'post-images');
    for (const id of ids || []) {
      const link = node('a');
      link.href = api + '/images/' + id;
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
      const image = node('img');
      image.src = link.href;
      image.alt = '玩家上传的截图';
      image.loading = 'lazy';
      link.append(image);
      wrap.append(link);
    }
    return wrap;
  }

  async function loadThread(id) {
    resetViews('detail');
    detail.replaceChildren(node('p', '', '正在读取讨论…'));
    try {
      const thread = await request('/threads/' + id);
      currentThread = thread.id;
      detail.replaceChildren();
      const back = node('button', 'back-button', '← 返回讨论');
      back.type = 'button';
      back.addEventListener('click', () => { location.hash = ''; });
      const meta = node('div', 'post-meta');
      meta.append(node('span', 'thread-category', thread.category), node('span', '', thread.nickname),
        node('time', '', time(thread.created_at)));
      const pill = statusPill(thread.status);
      if (pill) meta.append(pill);
      detail.append(back, meta, node('h2', '', thread.title), node('p', 'post-body', thread.body), imageList(thread.images));
      const section = node('section', 'reply-section');
      section.append(node('h3', '', '回复 · ' + thread.replies.length));
      for (const reply of thread.replies) {
        const card = node('article', 'reply-card');
        const replyMeta = node('div', 'post-meta');
        replyMeta.append(node('strong', '', reply.nickname), node('time', '', time(reply.created_at)));
        const replyPill = statusPill(reply.status);
        if (replyPill) replyMeta.append(replyPill);
        card.append(replyMeta, node('p', 'post-body', reply.body), imageList(reply.images));
        section.append(card);
      }
      if (thread.status === 'visible') {
        if (user) {
          const form = node('form', 'reply-form');
          form.append(node('label', '', '加入讨论'));
          const body = node('textarea');
          body.required = true; body.minLength = 2; body.maxLength = 5000; body.rows = 5;
          body.placeholder = '分享你的经验或补充复现步骤…';
          const files = node('input');
          files.type = 'file'; files.accept = 'image/png,image/jpeg,image/webp'; files.multiple = true;
          const hint = node('p', 'input-hint', '可附最多两张截图，每张不超过 3 MB。');
          const submit = node('button', 'button button-primary', '发布回复');
          submit.type = 'submit';
          form.append(body, files, hint, submit);
          form.addEventListener('submit', async (event) => {
            event.preventDefault(); submit.disabled = true;
            try {
              const images = await readImages(files.files);
              const result = await request('/threads/' + id + '/replies', {
                method: 'POST', body: JSON.stringify({ body: body.value, images }) });
              say(result.status === 'pending' ? '回复已提交，等待管理员审核。' : '回复已发布。');
              await loadThread(id);
            } catch (error) { say(error.message, true); }
            finally { submit.disabled = false; }
          });
          section.append(form);
        } else {
          const login = node('button', 'back-button', '登录后参与回复 →');
          login.type = 'button'; login.addEventListener('click', openAuth); section.append(login);
        }
      }
      detail.append(section);
    } catch (error) { detail.replaceChildren(); say(error.message, true); }
  }

  async function readImages(files) {
    if (files.length > 2) throw new Error('每条最多上传两张图片');
    const images = [];
    for (const file of files) {
      if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type) || file.size > 3 * 1024 * 1024)
        throw new Error('图片仅支持 PNG、JPEG、WebP，且每张不超过 3 MB');
      const data = await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result).split(',')[1]);
        reader.onerror = reject;
        reader.readAsDataURL(file);
      });
      images.push({ mime: file.type, data });
    }
    return images;
  }

  function openAuth() { dialog.showModal(); $('auth-nickname').focus(); }
  function setAuthMode(mode) {
    authMode = mode;
    $('login-tab').classList.toggle('active', mode === 'login');
    $('register-tab').classList.toggle('active', mode === 'register');
    $('auth-submit').textContent = mode === 'login' ? '登录' : '创建论坛账号';
    $('auth-password').autocomplete = mode === 'login' ? 'current-password' : 'new-password';
    $('auth-error').textContent = '';
  }

  async function loadModeration() {
    resetViews('moderation');
    $('moderation-list').replaceChildren(node('p', 'empty-state', '正在读取待审核内容…'));
    try {
      const pending = await request('/admin/pending');
      const box = $('moderation-list');
      box.replaceChildren();
      const items = [...pending.threads.map((x) => ({ ...x, kind: 'thread' })),
        ...pending.replies.map((x) => ({ ...x, kind: 'reply' }))];
      if (!items.length) box.append(node('p', 'empty-state', '没有待审核内容。'));
      for (const item of items) {
        const card = node('div', 'moderation-item');
        card.append(node('strong', '', (item.kind === 'thread' ? '讨论' : '回复') + ' · ' + item.nickname),
          node('p', '', (item.title ? item.title + '\n' : '') + item.body), imageList(item.images));
        for (const [decision, label] of [['approve', '通过'], ['reject', '拒绝']]) {
          const button = node('button', '', label); button.type = 'button';
          button.addEventListener('click', async () => {
            button.disabled = true;
            try {
              await request('/admin/moderate', { method: 'POST',
                body: JSON.stringify({ kind: item.kind, id: item.id, decision }) });
              await loadModeration();
            } catch (error) { say(error.message, true); button.disabled = false; }
          });
          card.append(button);
        }
        box.append(card);
      }
    } catch (error) { say(error.message, true); }
  }

  async function route() {
    const match = /^#thread\/([0-9]+)$/.exec(location.hash);
    if (match) await loadThread(match[1]);
    else await loadThreads();
  }

  $('account-button').addEventListener('click', async () => {
    if (!user) return openAuth();
    if (!confirm('退出当前论坛账号？')) return;
    try { await request('/logout', { method: 'POST', body: '{}' }); await refreshAccount(); await route(); }
    catch (error) { say(error.message, true); }
  });
  $('new-thread-button').addEventListener('click', () => { if (!user) return openAuth(); resetViews('composer'); say(''); });
  $('composer-close').addEventListener('click', () => { location.hash = ''; loadThreads(); });
  $('my-threads-button').addEventListener('click', () => { showingMine = !showingMine; $('my-threads-button').textContent = showingMine ? '所有讨论' : '我的帖子'; location.hash = ''; loadThreads(); });
  $('search-form').addEventListener('submit', (event) => { event.preventDefault(); showingMine = false; loadThreads(); });
  document.querySelectorAll('.forum-filters button').forEach((button) => button.addEventListener('click', () => {
    category = button.dataset.category; showingMine = false;
    document.querySelectorAll('.forum-filters button').forEach((item) => item.classList.toggle('active', item === button));
    loadThreads();
  }));
  $('thread-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const submit = $('thread-form').querySelector('button[type=submit]'); submit.disabled = true;
    try {
      const images = await readImages($('thread-images').files);
      const result = await request('/threads', { method: 'POST', body: JSON.stringify({
        category: $('thread-category').value, title: $('thread-title').value,
        body: $('thread-body').value, images }) });
      $('thread-form').reset();
      if (result.status === 'pending') {
        showingMine = true; $('my-threads-button').textContent = '所有讨论';
        say('已收到讨论，新账号首帖审核通过后会公开展示。');
        await loadThreads();
      } else location.hash = 'thread/' + result.id;
    } catch (error) { say(error.message, true); }
    finally { submit.disabled = false; }
  });
  $('login-tab').addEventListener('click', () => setAuthMode('login'));
  $('register-tab').addEventListener('click', () => setAuthMode('register'));
  $('auth-form').addEventListener('submit', async (event) => {
    event.preventDefault(); $('auth-submit').disabled = true;
    try {
      await request('/' + authMode, { method: 'POST', body: JSON.stringify({
        nickname: $('auth-nickname').value, password: $('auth-password').value }) });
      $('auth-password').value = '';
      dialog.close();
      await refreshAccount();
      say(authMode === 'register' ? '论坛账号已创建。你的第一条内容需要审核。' : '欢迎回来。');
      await route();
    } catch (error) { $('auth-error').textContent = error.message; }
    finally { $('auth-submit').disabled = false; }
  });
  $('moderation-refresh').addEventListener('click', loadModeration);
  window.addEventListener('hashchange', route);
  refreshAccount().then(route).catch((error) => say(error.message, true));
})();
