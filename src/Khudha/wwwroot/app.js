'use strict';
const byId = id => document.getElementById(id);
const state = { fields: [], snapshot: null, taskId: null, busy: false, lastAttempt: null };
const show = (id, visible) => { byId(id).hidden = !visible; };
const text = (id, value) => { byId(id).textContent = value ?? ''; };
function notice(message = '') { text('notice', message); show('notice', !!message); }
function busy(value, label = '') {
  state.busy = value;
  text('working-text', label);
  show('working', value);
  syncButtons();
}
function syncButtons() {
  for (const id of ['start-button', 'generate-button', 'ask-button', 'new-experience'])
    byId(id).disabled = state.busy;
  const task = state.snapshot?.currentTask;
  byId('review-button').disabled = state.busy || task?.status === 'Completed';
  byId('next-button').disabled = state.busy || task?.status !== 'Completed';
  byId('submission').readOnly = task?.status === 'Completed';
  byId('stage').disabled = state.busy;
  byId('field-select').disabled = state.busy || !byId('stage').value;
}
function populateFields(preferred) {
  const options = byId('stage').value ? state.fields.map(field => {
    const option = document.createElement('option');
    option.value = String(field.id); option.textContent = field.name; return option;
  }) : [Object.assign(document.createElement('option'), { value: '', textContent: 'اختر مرحلتك أولاً' })];
  byId('field-select').replaceChildren(...options);
  if (preferred) byId('field-select').value = String(preferred);
  syncButtons();
}
async function api(path, body) {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) })
  });
  let result;
  try { result = await response.json(); }
  catch { throw new Error('تعذر قراءة رد الموقع. تأكد من استمرار تشغيله ثم أعد المحاولة.'); }
  if (!response.ok) {
    const error = new Error(result.error || result.detail || 'تعذر إكمال الطلب. أعد المحاولة.');
    error.details = result.details;
    throw error;
  }
  return result;
}
function view(name) {
  show('home-view', name === 'home'); show('experience-view', name === 'experience');
  byId('home-nav').classList.toggle('active', name === 'home');
  byId('experience-nav').classList.toggle('active', name === 'experience');
  if (name === 'experience') window.scrollTo({ top: 0, behavior: 'smooth' });
}
function list(id, values) {
  byId(id).replaceChildren(...values.map(value => {
    const li = document.createElement('li'); li.textContent = value; return li;
  }));
}
function readDraft(taskId) {
  try { return localStorage.getItem('khudha.draft.' + taskId); } catch { return null; }
}
function saveDraft() {
  if (!state.taskId) return;
  try { localStorage.setItem('khudha.draft.' + state.taskId, byId('submission').value); }
  catch { /* Server-side submission saving still works if local storage is unavailable. */ }
}
function render(snapshot) {
  state.snapshot = snapshot;
  const session = snapshot.session;
  show('experience-nav', !!session); show('resume-banner', !!session);
  if (!session) { state.taskId = null; view('home'); return; }
  text('resume-description', `${session.field.name} · ${snapshot.progress.completed} مهام مكتملة`);
  text('field-title', session.field.name);
  text('student-label', `تجربتك العملية · ${session.stageLabel}`);
  text('field-overview', session.field.overview);
  text('completed-count', snapshot.progress.completed);
  byId('stage').value = session.stage;
  populateFields(session.field.id);

  const referenceNodes = (snapshot.references || []).map(reference => {
    const li = document.createElement('li'); const a = document.createElement('a');
    try {
      const url = new URL(reference.url);
      if (url.protocol !== 'https:') return li;
      a.href = url.href;
    } catch { return li; }
    a.textContent = reference.title; a.target = '_blank'; a.rel = 'noopener noreferrer';
    li.append(a); return li;
  });
  byId('references').replaceChildren(...referenceNodes);

  const task = snapshot.currentTask;
  show('empty-task', !task); show('workspace', !!task);
  for (const id of ['step-intro', 'step-task', 'step-review', 'step-next'])
    byId(id).classList.remove('current', 'done');
  byId('step-intro').classList.add(task ? 'done' : 'current');
  if (!task) { state.taskId = null; syncButtons(); return; }

  const changed = state.taskId !== task.id;
  state.taskId = task.id;
  const content = task.content;
  text('task-number', `المهمة ${task.number.toLocaleString('ar')}`);
  text('task-title', content.title);
  text('task-source', content.source === 'template' ? 'قالب جاهز بديل' : 'جهّزها الوكيل لك');
  show('fallback-note', content.source === 'template');
  list('instructions', content.instructions);
  list('criteria', content.criteria);
  text('deliverable', content.deliverable); text('starter-hint', content.hint);

  const attempt = snapshot.latestAttempt;
  state.lastAttempt = attempt ? { id: attempt.id, submission: attempt.submission, status: attempt.status } : null;
  if (changed) {
    byId('submission').value = readDraft(task.id) ?? attempt?.submission ?? content.starterCode ?? '';
    byId('question').value = '';
    text('draft-status', 'تُحفظ المسودة في هذا المتصفح.');
  }
  const pending = attempt && ['Pending', 'Failed'].includes(attempt.status);
  show('saved-status', !!attempt);
  text('saved-status', pending ? 'محاولتك محفوظة. يمكنك إعادة طلب المراجعة.' : 'تم حفظ محاولتك ومراجعتها.');
  text('review-button', pending ? 'إعادة المراجعة ←' : task.status === 'Completed' ? 'اكتملت المهمة ✓' : 'راجع محاولتي ←');

  const review = attempt?.review;
  show('review-card', !!review);
  show('next-card', task.status === 'Completed');
  byId('step-task').classList.add(review ? 'done' : 'current');
  if (review) {
    text('review-status', review.completed ? 'حققت معايير المهمة ✓' : 'خطوة أخرى وتوصل');
    list('strengths', review.strengths); list('improvements', review.improvements);
    show('strengths-section', review.strengths.length > 0);
    show('improvements-section', review.improvements.length > 0);
    text('review-hint', review.hint);
    byId('step-review').classList.add(review.completed ? 'done' : 'current');
  }
  if (task.status === 'Completed') byId('step-next').classList.add('current');

  const history = (snapshot.history || []).map(item => {
    const li = document.createElement('li');
    const label = document.createElement('span'); const status = document.createElement('span');
    label.textContent = `${item.number}. ${item.title}`;
    status.textContent = item.status === 'Completed' ? 'مكتملة ✓' : 'قيد التجربة';
    status.classList.toggle('complete', item.status === 'Completed');
    li.append(label, status); return li;
  });
  byId('history').replaceChildren(...history);
  const messages = snapshot.messages?.length ? snapshot.messages : [
    { role: 'assistant', content: 'أنا معك في هذه المهمة. اسأل عن خطوة غير واضحة، وسأساعدك بشرح أو تلميح لتخوض المحاولة بنفسك.' }
  ];
  byId('messages').replaceChildren(...messages.map(message => {
    const bubble = document.createElement('div');
    bubble.className = 'message ' + (message.role === 'user' ? 'user' : 'assistant');
    const speaker = document.createElement('span'); speaker.className = 'speaker';
    speaker.textContent = message.role === 'user' ? 'أنت' : 'وكيل خُضها';
    const body = document.createElement('span'); body.textContent = message.content;
    bubble.append(speaker, body); return bubble;
  }));
  byId('messages').scrollTop = byId('messages').scrollHeight;
  syncButtons();
}

byId('home-nav').addEventListener('click', () => view('home'));
byId('experience-nav').addEventListener('click', () => view('experience'));
byId('resume-button').addEventListener('click', () => view('experience'));
byId('new-experience').addEventListener('click', () => { view('home'); byId('stage').focus(); });
byId('stage').addEventListener('change', () => populateFields());
byId('submission').addEventListener('input', () => {
  saveDraft(); text('draft-status', 'مسودتك محفوظة في هذا المتصفح.');
});

byId('start-form').addEventListener('submit', async event => {
  event.preventDefault(); if (state.busy) return;
  if (state.snapshot?.session && !window.confirm('بدء تجربة جديدة سيغيّر التجربة التي تتابعها في هذا المتصفح. هل تريد المتابعة؟')) return;
  notice(); busy(true, 'نجهّز مساحة تجربتك…');
  try {
    const snapshot = await api('/api/session', { stage: byId('stage').value, fieldId: Number(byId('field-select').value) });
    render(snapshot); view('experience');
  } catch (error) { notice(error.message); }
  finally { busy(false); }
});

async function generate() {
  if (state.busy) return;
  notice(); saveDraft(); busy(true, 'الوكيل يجهّز مهمتك بالاستناد إلى المراجع…');
  try { render(await api('/api/tasks', {})); view('experience'); }
  catch (error) { notice(error.message); }
  finally { busy(false); }
}
byId('generate-button').addEventListener('click', generate);
byId('next-button').addEventListener('click', generate);

byId('attempt-form').addEventListener('submit', async event => {
  event.preventDefault(); if (state.busy || !state.taskId) return;
  const submission = byId('submission').value.trim();
  if (!submission) { notice('اكتب محاولتك قبل إرسالها للمراجعة.'); return; }
  const existing = state.lastAttempt;
  const requestId = existing && existing.submission === submission && ['Pending', 'Failed'].includes(existing.status)
    ? existing.id : crypto.randomUUID();
  state.lastAttempt = { id: requestId, submission, status: 'Pending' };
  saveDraft(); notice(); busy(true, 'نحفظ محاولتك، والوكيل يراجعها وفق المعايير…');
  try {
    render(await api(`/api/tasks/${state.taskId}/attempts`, { requestId, submission }));
    byId('review-card').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  } catch (error) {
    if (error.details?.attemptId) state.lastAttempt.id = error.details.attemptId;
    notice(error.message);
    try { render(await api('/api/session')); } catch { /* Keep the local draft if the server is unavailable. */ }
  } finally { busy(false); }
});

byId('question-form').addEventListener('submit', async event => {
  event.preventDefault(); if (state.busy || !state.taskId) return;
  const question = byId('question').value.trim();
  if (!question) { notice('اكتب سؤالك أولاً.'); return; }
  notice(); busy(true, 'الوكيل يفكّر معك في الخطوة التالية…');
  try {
    render(await api(`/api/tasks/${state.taskId}/hint`, { question, draft: byId('submission').value }));
    byId('question').value = '';
  } catch (error) {
    notice(error.message);
    try { render(await api('/api/session')); } catch { }
  } finally { busy(false); }
});

async function boot() {
  busy(true, 'نجهّز تجربتك…'); notice();
  try {
    state.fields = await api('/api/fields');
    populateFields();
    if (!state.fields.length) throw new Error('لا توجد مجالات جاهزة في قاعدة البيانات.');
    const snapshot = await api('/api/session'); render(snapshot);
    if (snapshot.session) view('experience');
  } catch (error) { notice(error.message); }
  finally { busy(false); }
}
boot();
