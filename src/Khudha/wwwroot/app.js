
'use strict';
const byId = id => document.getElementById(id);
const state = { catalog: [], snapshot: null, sessionId: null, taskId: null, busy: false,
  lastAttempt: null, majorOptions: [], majorActive: -1, orientationOpen: true, homeMajor: null };

function createRequestId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  // randomUUID may be unavailable on HTTP; getRandomValues remains available in modern browsers.
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  // Last-resort compatibility fallback for very old browsers without Web Crypto.
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, char => {
    const random = Math.floor(Math.random() * 16);
    const value = char === 'x' ? random : (random & 0x03) | 0x08;
    return value.toString(16);
  });
}

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
  for (const id of ['start-button', 'generate-button', 'ask-button', 'explore-ask-button', 'new-experience', 'back-to-overview', 'repair-session-button', 'finish-experience'])
    byId(id).disabled = state.busy;
  const task = state.snapshot?.currentTask;
  byId('review-button').disabled = state.busy || task?.status === 'Completed';
  byId('next-button').disabled = state.busy || task?.status !== 'Completed' || !!state.snapshot?.experienceComplete;
  byId('submission').readOnly = state.busy || task?.status === 'Completed';
  byId('question').readOnly = state.busy;
  byId('explore-question').readOnly = state.busy;
  byId('stage').disabled = state.busy;
  byId('major-input').disabled = state.busy || !byId('stage').value;
  byId('major-toggle').disabled = byId('major-input').disabled;
  byId('start-button').disabled = state.busy || state.catalog.length !== 2;
  for (const id of ['home-major-cs', 'home-major-business'])
    byId(id).disabled = state.busy || state.catalog.length !== 2;
  byId('orientation-generate-button').disabled = state.busy || !state.snapshot?.exploration?.canGenerate || !state.snapshot?.learningPlan;
  if (state.snapshot?.routingIssue) {
    for (const id of ['generate-button', 'review-button', 'next-button', 'ask-button', 'explore-ask-button', 'orientation-generate-button'])
      byId(id).disabled = true;
  }
  for (const card of byId('track-options').children)
    card.disabled = state.busy || !state.snapshot?.exploration?.canChooseTrack;
  if (byId('major-input').disabled) closeMajorOptions();
}
function updateMajorField(preferred = '') {
  const stage = byId('stage').value;
  text('major-label', stage === 'university' ? 'تخصصك الجامعي' : stage === 'graduate' ? 'التخصص الذي تريد استكشافه' : 'التخصص');
  byId('major-input').placeholder = stage === 'graduate' ? 'حدد التخصص' : stage === 'university' ? 'حدد تخصصك' : 'اختر مرحلتك أولاً';
  byId('major-input').value = preferred;
  byId('major-input').setCustomValidity('');
  closeMajorOptions();
  syncButtons();
}
function normalizeMajor(value) {
  return value.normalize('NFKC').replace(/[\u064B-\u065F\u0670]/g, '')
    .replace(/[أإآٱ]/g, 'ا').replace(/ى/g, 'ي').replace(/ة/g, 'ه').toLocaleLowerCase().trim();
}
function matchingMajor(value) {
  const query = normalizeMajor(value);
  return state.catalog.find(major => [major.name, major.slug, ...(major.aliases || [])]
    .some(name => normalizeMajor(name) === query));
}
function closeMajorOptions() {
  show('major-options', false);
  byId('major-input').setAttribute('aria-expanded', 'false');
  byId('major-input').removeAttribute('aria-activedescendant');
  state.majorActive = -1;
}
function openMajorOptions(filterByInput = false) {
  const input = byId('major-input');
  if (input.disabled) return;
  const query = filterByInput ? normalizeMajor(input.value) : '';
  state.majorOptions = state.catalog.filter(major => [major.name, ...(major.aliases || [])]
    .some(name => normalizeMajor(name).includes(query)));
  state.majorActive = -1;
  input.removeAttribute('aria-activedescendant');
  const nodes = state.majorOptions.map((major, index) => {
    const option = document.createElement('div');
    option.id = `major-option-${index}`;
    option.className = 'major-option';
    option.setAttribute('role', 'option');
    option.setAttribute('aria-selected', 'false');
    option.textContent = major.name;
    option.addEventListener('click', () => chooseMajor(major.name));
    return option;
  });
  if (!nodes.length) {
    const note = document.createElement('div');
    note.className = 'major-empty';
    note.textContent = 'ما فيه تخصص مطابق. افتح القائمة لعرض التخصصات المتاحة.';
    nodes.push(note);
  }
  byId('major-options').replaceChildren(...nodes);
  show('major-options', true);
  input.setAttribute('aria-expanded', 'true');
}
function activateMajor(index) {
  if (!state.majorOptions.length) return;
  state.majorActive = (index + state.majorOptions.length) % state.majorOptions.length;
  const options = byId('major-options').querySelectorAll('[role="option"]');
  options.forEach((option, i) => option.setAttribute('aria-selected', String(i === state.majorActive)));
  const active = options[state.majorActive];
  byId('major-input').setAttribute('aria-activedescendant', active.id);
  active.scrollIntoView({ block: 'nearest' });
}
function chooseMajor(major) {
  state.homeMajor = null;
  byId('major-input').value = major;
  byId('major-input').setCustomValidity('');
  byId('major-input').focus();
  closeMajorOptions();
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
  byId('main-navigation').classList.remove('open');
  byId('nav-menu-toggle').setAttribute('aria-expanded', 'false');
  if (name === 'experience') window.scrollTo({ top: 0, behavior: 'smooth' });
}
function openHomeSection(id) {
  view('home');
  byId(id).scrollIntoView({ behavior: 'smooth', block: 'start' });
}
function exploreHomeMajor(slug) {
  if (state.busy) return;
  const major = matchingMajor(slug);
  if (!major) return;
  openHomeSection('start-section');
  if (!byId('stage').value) {
    // Remember the card only until the user chooses their education stage.
    state.homeMajor = major.name;
    byId('stage').focus();
  } else {
    updateMajorField(major.name);
    chooseMajor(major.name);
  }
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
  if (!state.taskId || state.busy || state.snapshot?.currentTask?.status === 'Completed') return false;
  try { localStorage.setItem('khudha.draft.' + state.taskId, byId('submission').value); return true; }
  catch { return false; /* The server still saves submitted attempts. */ }
}
function renderReferences(id, references) {
  byId(id).replaceChildren(...(references || []).map(reference => {
    const li = document.createElement('li'); const a = document.createElement('a');
    try {
      const url = new URL(reference.url);
      if (url.protocol !== 'https:') return li;
      a.href = url.href;
    } catch { return li; }
    a.textContent = reference.title; a.target = '_blank'; a.rel = 'noopener noreferrer';
    li.append(a); return li;
  }));
}
function renderMessages(id, messages, greeting) {
  const content = messages?.length ? messages : [{ role: 'assistant', content: greeting }];
  byId(id).replaceChildren(...content.map(message => {
    const bubble = document.createElement('div');
    bubble.className = 'message ' + (message.role === 'user' ? 'user' : 'assistant');
    const speaker = document.createElement('span'); speaker.className = 'speaker';
    speaker.textContent = message.role === 'user' ? 'أنت' : 'وكيل خُضها';
    const body = document.createElement('span'); body.textContent = message.content;
    bubble.append(speaker, body); return bubble;
  }));
  byId(id).scrollTop = byId(id).scrollHeight;
}
function renderLearningPlan(plan) {
  show('learning-plan-card', !!plan);
  if (!plan) return;
  text('learning-task-count', `${plan.taskCount} مهام متدرجة`);
  text('learning-plan-title', plan.lesson.title);
  text('learning-plan-goal', plan.lesson.goal);
  byId('lesson-sections').replaceChildren(...plan.lesson.sections.map(section => {
    const block = document.createElement('section');
    const title = document.createElement('h3'); title.textContent = section.title;
    const ul = document.createElement('ul');
    for (const item of section.items) {
      const li = document.createElement('li'); li.textContent = item; ul.append(li);
    }
    block.append(title, ul); return block;
  }));
  const example = plan.lesson.example;
  text('lesson-example-title', example.title);
  text('lesson-example-scenario', example.scenario);
  list('lesson-example-steps', example.steps);
  text('lesson-example-code', example.code || '');
  show('lesson-example-code-wrap', !!example.code);
  text('lesson-example-takeaway', example.takeaway);
  byId('learning-task-outline').replaceChildren(...plan.tasks.map(task => {
    const li = document.createElement('li');
    const title = document.createElement('strong'); title.textContent = task.title;
    const deliverable = document.createElement('span'); deliverable.textContent = task.deliverable;
    li.append(title, deliverable); return li;
  }));
  list('learning-mechanism', plan.lesson.mechanism);
}
function renderOrientation(exploration, task) {
  const university = state.snapshot.session.stage === 'university';
  const major = exploration.major;
  renderLearningPlan(state.snapshot.learningPlan);
  show('learning-plan-prompt', university && !state.snapshot.learningPlan);
  text('orientation-eyebrow', university ? 'اكتشف الاتجاه الذي يناسب اهتمامك' : 'ابدأ من الصفر، وخذ وقتك في الفهم');
  text('orientation-title', university ? `مسارات ${major.name}` : `تعرّف على ${major.name}`);
  text('major-summary', major.summary);
  byId('major-sections').replaceChildren(...(university ? [] : major.sections).map(section => {
    const article = document.createElement('section');
    const heading = document.createElement('h3'); heading.textContent = section.title;
    const ul = document.createElement('ul');
    for (const item of section.items) {
      const li = document.createElement('li'); li.textContent = item; ul.append(li);
    }
    article.append(heading, ul); return article;
  }));
  show('tracks-section', university);
  text('tracks-help', exploration.canChooseTrack
    ? 'تعرّف على الخيارين واسأل الوكيل، ثم اختر المسار الذي تريد تجربته.'
    : 'هذا مسارك الحالي. يمكنك قراءة الخيارات، وبدء تجربة جديدة إذا أردت تغيير المسار.');
  byId('track-options').replaceChildren(...(university ? major.tracks : []).map((track, index) => {
    const selected = exploration.selectedTrack?.slug === track.slug;
    const card = document.createElement('button');
    card.type = 'button'; card.className = 'track-card';
    card.setAttribute('aria-pressed', String(selected));
    card.setAttribute('aria-label', `اختيار مسار ${track.name}`);
    card.disabled = state.busy || !exploration.canChooseTrack;
    const top = document.createElement('span'); top.className = 'track-card-top';
    const number = document.createElement('span'); number.textContent = `0${index + 1}`;
    const status = document.createElement('span'); status.className = 'track-state';
    status.textContent = selected ? 'مسارك المحدد ✓' : exploration.canChooseTrack ? 'اختر هذا المسار' : 'مسار آخر';
    top.append(number, status);
    const name = document.createElement('strong'); name.className = 'track-name'; name.textContent = track.name;
    const description = document.createElement('span'); description.className = 'track-description'; description.textContent = track.description;
    const example = document.createElement('span'); example.className = 'track-example'; example.textContent = 'في تجربتك: ' + track.example;
    const skills = document.createElement('span'); skills.className = 'track-skills'; skills.textContent = track.skills.join(' · ');
    const fit = document.createElement('span'); fit.className = 'track-fit'; fit.textContent = track.fit;
    const illustrations = {
      'web-development': 'web-development', 'data-analysis': 'data-analysis',
      'marketing': 'marketing', 'operations': 'operations'
    };
    if (Object.hasOwn(illustrations, track.slug)) {
      const cover = document.createElement('span'); cover.className = 'track-art';
      const image = document.createElement('img');
      image.src = `/assets/khudha/${illustrations[track.slug]}.svg`;
      image.alt = ''; image.setAttribute('aria-hidden', 'true');
      cover.append(image); card.append(cover);
    }
    card.append(top, name, description, example, skills, fit);
    card.addEventListener('click', () => selectTrack(track.slug));
    return card;
  }));
  text('orientation-next-title', task ? 'تجربتك محفوظة وبانتظارك' : university
    ? exploration.selectedTrack ? `مسارك: ${exploration.selectedTrack.name}` : 'اختر مسارك بعد أن تتعرف عليه'
    : 'فهمت الفكرة؟ خُض أول تجربة');
  text('orientation-next-description', task ? 'ارجع إلى مهمتك الحالية وتابع من حيث توقفت.' : university
    ? exploration.selectedTrack ? 'جاهز؟ نبدأ بمهمة عملية تمهيدية في المسار الذي اخترته.' : 'يمكنك سؤال الوكيل عن الفروق قبل الاختيار.'
    : 'اسأل الوكيل عن أي نقطة جديدة عليك، ثم ابدأ مهمة مبسطة دون الحاجة إلى تثبيت برامج.');
  text('orientation-generate-button', task ? 'متابعة المهام ←' : 'فهمت الآلية، جهّز مهمتي الأولى ←');
  text('explore-chat-subtitle', university ? 'اسأل عن المسارات والفروق بينها' : 'نفهم التخصص معك، خطوة بخطوة');
  byId('explore-question').placeholder = university ? 'وش الفرق بين المسارين؟ وكيف أختار تجربتي؟' : 'اسأل عن التخصص أو عن مصطلح جديد عليك…';
  renderMessages('explore-messages', exploration.messages, university
    ? `أهلاً بك في استكشاف مسارات ${major.name}. اسأل عن طبيعة العمل أو الفروق بين الخيارين، ثم اختر المسار الذي تود تجربته بنفسك.`
    : `أهلاً بك في ${major.name}. لا تحتاج معرفة سابقة؛ اسأل عن أي نقطة في التعريف وسأشرحها لك بمثال بسيط قبل أن تبدأ مهمتك.`);
  renderReferences('exploration-references', exploration.references);
}
function render(snapshot) {
  state.snapshot = snapshot;
  const session = snapshot.session;
  show('experience-nav', !!session); show('resume-banner', !!session);
  if (!session) { state.sessionId = null; state.taskId = null; view('home'); return; }
  if (state.sessionId !== session.id) {
    state.sessionId = session.id;
    state.orientationOpen = !snapshot.currentTask;
    byId('explore-question').value = '';
  }
  const task = snapshot.currentTask;
  const repair = !!snapshot.routingIssue;
  const exploring = !repair && !!snapshot.exploration && (state.orientationOpen || !task);
  show('routing-repair', repair);
  text('routing-repair-message', snapshot.routingIssue || '');
  show('orientation-workspace', exploring); show('task-experience', !exploring);
  if (repair) show('task-experience', false);
  show('back-to-overview', !!snapshot.exploration && !!task && !exploring);
  text('resume-description', `${session.major ? session.major + ' · ' : ''}${snapshot.exploration?.selectedTrack?.name || session.field?.name || 'استكشاف المسارات'} · ${snapshot.progress.completed} مهام مكتملة`);
  text('field-title', exploring ? session.major : session.field?.name || session.major);
  text('student-label', `تجربتك العملية · ${session.stageLabel}`);
  text('profile-major', session.major ? `${session.stage === 'university' ? 'تخصصك الجامعي' : 'التخصص الذي تستكشفه'}: ${session.major}` : '');
  show('profile-major', !!session.major);
  text('field-overview', session.field?.overview || snapshot.exploration?.major.summary || '');
  text('completed-count', snapshot.progress.completed);
  text('progress-caption', `مكتملة من ${snapshot.progress.total || snapshot.learningPlan?.taskCount || 3}`);
  byId('experience-progress').max = snapshot.progress.total || snapshot.learningPlan?.taskCount || 3;
  byId('experience-progress').value = snapshot.progress.completed;
  byId('experience-progress').setAttribute('aria-valuetext', `${snapshot.progress.completed} مهام مكتملة من ${byId('experience-progress').max}`);
  byId('stage').value = session.stage === 'high-school' ? 'graduate' : session.stage;
  updateMajorField(session.major ?? '');
  if (repair) {
    text('field-title', session.major || 'تجربتك');
    text('resume-description', `${session.major || 'تجربة سابقة'} · تحتاج بدء التجربة الصحيحة`);
    show('back-to-overview', false);
    state.taskId = null; state.lastAttempt = null; syncButtons(); return;
  }
  if (snapshot.exploration) renderOrientation(snapshot.exploration, task);
  renderReferences('references', snapshot.references);
  const code = snapshot.inputKind === 'code' || session.field?.slug === 'web-development';
  text('submission-kind', code ? 'الكود كنص' : 'محاولتك كنص');
  byId('submission').dir = code ? 'ltr' : 'auto';
  byId('submission').placeholder = code ? 'اكتب كود HTML هنا…' : 'اكتب محاولتك وخطوات تفكيرك هنا…';
  byId('submission').classList.toggle('text-submission', !code);
  show('empty-task', !task); show('workspace', !!task);
  for (const id of ['step-intro', 'step-task', 'step-review', 'step-next'])
    byId(id).classList.remove('current', 'done');
  byId('step-intro').classList.add(task ? 'done' : 'current');
  if (!task) { state.taskId = null; syncButtons(); return; }

  const changed = state.taskId !== task.id;
  state.taskId = task.id;
  const content = task.content;
  text('task-number', `المهمة ${task.number.toLocaleString('ar')} من ${snapshot.progress.total || snapshot.learningPlan?.taskCount || 3}`);
  text('task-title', content.title);
  text('task-source', content.source === 'template' ? 'مهمة وتلميح محضّران' : content.source === 'guided' ? 'مهمة محضّرة بتلميح الوكيل' : 'جهّزها الوكيل لك');
  show('fallback-note', content.source === 'template');
  list('instructions', content.instructions);
  list('criteria', content.criteria);
  text('deliverable', content.deliverable); text('starter-hint', content.hint);

  const attempt = snapshot.latestAttempt;
  state.lastAttempt = attempt ? { id: attempt.id, submission: attempt.submission, status: attempt.status } : null;
  const accepted = task.status === 'Completed' && attempt?.status === 'Reviewed' && attempt.review?.completed;
  if (accepted) {
    // The visible answer must be the exact submission associated with this review.
    byId('submission').value = attempt.submission;
    try { localStorage.setItem('khudha.draft.' + task.id, attempt.submission); } catch { }
    text('draft-status', 'هذه الإجابة المحفوظة التي تمت مراجعتها وقبولها.');
    if (changed) byId('question').value = '';
  } else if (changed) {
    // Older saved tasks may include a previous written answer as starterCode.
    // Keep real drafts/attempts, and only prefill code for the cumulative web tasks.
    byId('submission').value = readDraft(task.id) ?? attempt?.submission ?? (code ? content.starterCode : '') ?? '';
    byId('question').value = '';
    text('draft-status', 'تُحفظ المسودة في هذا المتصفح.');
  }
  const pending = attempt && ['Pending', 'Failed'].includes(attempt.status);
  show('saved-status', !!attempt);
  text('saved-status', pending ? 'محاولتك محفوظة. يمكنك إعادة طلب المراجعة.' : 'تم حفظ محاولتك ومراجعتها.');
  text('review-button', pending ? 'إعادة المراجعة ←' : task.status === 'Completed' ? 'اكتملت المهمة ✓' : 'راجع محاولتي ←');

  const review = attempt?.review;
  byId('review-card').classList.toggle('review-complete', !!review?.completed);
  show('review-card', !!review);
  show('next-card', task.status === 'Completed' && !snapshot.experienceComplete);
  show('course-complete-card', !!snapshot.experienceComplete);
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
  renderMessages('messages', snapshot.messages, 'أنا معك في هذه المهمة. اسأل عن خطوة غير واضحة، وسأساعدك بشرح أو تلميح لتخوض المحاولة بنفسك.');
  syncButtons();
}

byId('home-nav').addEventListener('click', () => view('home'));
byId('brand-home').addEventListener('click', () => view('home'));
byId('footer-home').addEventListener('click', () => { view('home'); window.scrollTo({ top: 0, behavior: 'smooth' }); });
byId('nav-explore').addEventListener('click', () => openHomeSection('home-majors'));
byId('nav-how').addEventListener('click', () => openHomeSection('how'));
byId('footer-how').addEventListener('click', () => openHomeSection('how'));
for (const id of ['nav-start', 'footer-start'])
  byId(id).addEventListener('click', () => {
    openHomeSection('start-section');
    if (!state.busy) byId(byId('stage').value ? 'major-input' : 'stage').focus();
  });
byId('nav-menu-toggle').addEventListener('click', () => {
  const open = byId('nav-menu-toggle').getAttribute('aria-expanded') !== 'true';
  byId('nav-menu-toggle').setAttribute('aria-expanded', String(open));
  byId('main-navigation').classList.toggle('open', open);
});
for (const id of ['main-navigation', 'nav-menu-toggle'])
  byId(id).addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      byId('main-navigation').classList.remove('open');
      byId('nav-menu-toggle').setAttribute('aria-expanded', 'false');
      byId('nav-menu-toggle').focus();
    }
  });
byId('home-major-cs').addEventListener('click', () => exploreHomeMajor('computer-science'));
byId('home-major-business').addEventListener('click', () => exploreHomeMajor('business-administration'));
byId('experience-nav').addEventListener('click', () => view('experience'));
byId('resume-button').addEventListener('click', () => view('experience'));
byId('new-experience').addEventListener('click', () => { view('home'); byId('stage').focus(); });
byId('finish-experience').addEventListener('click', () => { view('home'); byId('stage').focus(); });
byId('repair-session-button').addEventListener('click', async () => {
  if (state.busy) return;
  const session = state.snapshot?.session;
  const major = matchingMajor(session?.majorSlug || session?.major || '');
  if (!major) { view('home'); byId('stage').focus(); return; }
  notice(); busy(true, 'نبدأ تجربة جديدة تطابق تخصصك…');
  try {
    render(await api('/api/session', { stage: session.stage === 'university' ? 'university' : 'graduate', majorSlug: major.slug }));
    view('experience');
  } catch (error) { notice(error.message); }
  finally { busy(false); }
});
byId('back-to-overview').addEventListener('click', () => {
  saveDraft(); state.orientationOpen = true; render(state.snapshot); view('experience');
});
byId('stage').addEventListener('change', () => {
  const suggested = state.homeMajor;
  state.homeMajor = null;
  updateMajorField(suggested || '');
});
byId('major-input').addEventListener('focus', () => openMajorOptions());
byId('major-input').addEventListener('click', () => openMajorOptions());
byId('major-input').addEventListener('input', () => {
  byId('major-input').setCustomValidity('');
  openMajorOptions(true);
});
byId('major-input').addEventListener('keydown', event => {
  if (event.isComposing) return;
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    if (byId('major-options').hidden) openMajorOptions();
    const index = state.majorActive === -1
      ? (event.key === 'ArrowDown' ? 0 : state.majorOptions.length - 1)
      : state.majorActive + (event.key === 'ArrowDown' ? 1 : -1);
    activateMajor(index);
  } else if (event.key === 'Enter' && !byId('major-options').hidden) {
    event.preventDefault();
    if (state.majorActive >= 0) chooseMajor(state.majorOptions[state.majorActive].name);
    else closeMajorOptions();
  } else if (event.key === 'Escape') {
    if (!byId('major-options').hidden) event.preventDefault();
    closeMajorOptions();
  } else if (event.key === 'Tab') closeMajorOptions();
});
byId('major-toggle').addEventListener('click', () => {
  if (!byId('major-options').hidden) closeMajorOptions();
  else { byId('major-input').focus(); openMajorOptions(); }
});
byId('major-options').addEventListener('pointerdown', event => event.preventDefault());
byId('major-combobox').addEventListener('focusout', event => {
  if (!byId('major-combobox').contains(event.relatedTarget)) closeMajorOptions();
});
document.addEventListener('pointerdown', event => {
  if (!byId('major-combobox').contains(event.target)) closeMajorOptions();
});
byId('submission').addEventListener('input', () => {
  const saved = saveDraft();
  if (!byId('submission').readOnly)
    text('draft-status', saved ? 'مسودتك محفوظة في هذا المتصفح.' : 'تعذر الحفظ المحلي؛ انسخ مسودتك احتياطياً قبل مغادرة الصفحة.');
});

byId('start-form').addEventListener('submit', async event => {
  event.preventDefault(); if (state.busy) return;
  const major = matchingMajor(byId('major-input').value);
  if (!major) {
    byId('major-input').setCustomValidity('اختر تخصصًا من الاقتراحات المتاحة للمتابعة.');
    byId('major-input').reportValidity();
    return;
  }
  if (state.snapshot?.session && !window.confirm('بدء تجربة جديدة سيغيّر التجربة التي تتابعها في هذا المتصفح. هل تريد المتابعة؟')) return;
  notice(); busy(true, 'نجهّز مساحة تجربتك…');
  try {
    const snapshot = await api('/api/session', { stage: byId('stage').value, majorSlug: major.slug });
    render(snapshot); view('experience');
  } catch (error) { notice(error.message); }
  finally { busy(false); }
});

async function generate() {
  if (state.busy || state.snapshot?.routingIssue || state.snapshot?.experienceComplete) return;
  notice(); saveDraft(); busy(true, 'الوكيل يجهّز مهمتك بالاستناد إلى المراجع…');
  try {
    const snapshot = await api('/api/tasks', { lessonSlug: state.snapshot?.learningPlan?.experienceSlug });
    state.orientationOpen = false;
    render(snapshot); view('experience');
  }
  catch (error) { notice(error.message); }
  finally { busy(false); }
}
byId('generate-button').addEventListener('click', generate);
byId('next-button').addEventListener('click', generate);
byId('orientation-generate-button').addEventListener('click', () => {
  if (state.busy) return;
  if (state.snapshot?.currentTask) {
    state.orientationOpen = false; render(state.snapshot); view('experience');
  } else generate();
});

async function selectTrack(trackSlug) {
  if (state.busy || !state.snapshot?.exploration?.canChooseTrack) return;
  notice(); busy(true, 'نحفظ المسار الذي اخترته…');
  try {
    render(await api('/api/session/track', { trackSlug }));
    byId('learning-plan-card').scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
  catch (error) { notice(error.message); }
  finally { busy(false); }
}

byId('explore-question-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (state.busy || !state.snapshot?.exploration) return;
  const question = byId('explore-question').value.trim();
  if (!question || question.length > 1000) { notice('اكتب سؤالك في حد أقصى 1000 حرف.'); return; }
  notice(); busy(true, state.snapshot.session.stage === 'university' ? 'الوكيل يوضح لك المسارات…' : 'الوكيل يوضح لك التخصص…');
  try {
    render(await api('/api/session/chat', { question }));
    byId('explore-question').value = '';
  } catch (error) {
    notice(error.message);
    try { render(await api('/api/session')); } catch { }
  } finally { busy(false); }
});

byId('attempt-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (state.busy || !state.taskId || state.snapshot?.currentTask?.status !== 'Active') return;
  const submission = byId('submission').value.trim();
  if (!submission) { notice('اكتب محاولتك قبل إرسالها للمراجعة.'); return; }
  const existing = state.lastAttempt;
  const requestId = existing && existing.submission === submission && ['Pending', 'Failed'].includes(existing.status)
    ? existing.id : createRequestId();
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
    state.catalog = await api('/api/catalog');
    updateMajorField();
    if (state.catalog.length !== 2) throw new Error('تعذر تجهيز التخصصين. تأكد من اتصال قاعدة البيانات ثم أعد التحميل.');
    const snapshot = await api('/api/session'); render(snapshot);
    if (snapshot.session) view('experience');
  } catch (error) { notice(error.message); }
  finally { busy(false); }
}
boot();
