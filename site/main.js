// Nodekeeper · site. Sem dependências: idioma, versão mais recente, vídeo e ampliar capturas.
(() => {
  'use strict';

  const REPO = 'acboer/Nodekeeper';

  // ---------- Português (o inglês fica no HTML) ----------
  const PT = {
    'nav.features': 'Recursos', 'nav.video': 'Vídeo', 'nav.install': 'Instalar', 'nav.trust': 'Privacidade e segurança',
    'nav.download': 'Baixar',
    'hero.eyebrow': 'Gratuito e de código aberto · Windows 10/11',
    'hero.title': 'Um guardião para <span class="grad">todos os seus PCs.</span>',
    'hero.lead': 'Temperaturas, uso, ventoinhas, discos, rede, bateria, FPS nos jogos e benchmarks do seu PC gamer, do seu NAS e dos seus nós de render. Num painel só e num widget sempre visível na área de trabalho.',
    'hero.download': 'Baixar para Windows', 'hero.github': 'Ver no GitHub',
    'hero.meta': 'Última versão no GitHub', 'hero.meta2': '64 bits · inglês e português · licença MIT',
    'pill.lan': 'Só na sua rede', 'pill.accounts': 'Sem contas', 'pill.telemetry': 'Sem telemetria', 'pill.anticheat': 'Sem risco de anti-cheat',
    'video.title': 'Veja em 30 segundos', 'video.play': 'Assistir ao vídeo',
    'features.title': 'Tudo o que seus PCs estão fazendo, num lugar só',
    'features.sub': 'Instale o serviço em cada PC e acompanhe todos de um painel só.',
    'f1.t': 'Painel ao vivo', 'f1.d': 'CPU, GPU, RAM, discos, volumes, rede, ventoinhas, energia e periféricos, com gráficos de 24 horas e estados amarelo/vermelho para calor, disco cheio e disco com defeito.',
    'f2.t': 'Aba Desempenho', 'f2.d': 'Gráficos no estilo do Gerenciador de Tarefas para CPU (geral ou por núcleo), memória, discos, rede e motores da GPU, com a temperatura sobre o uso e visões de 10 min / 1 h / 24 h.',
    'f3.t': 'Todas as suas máquinas', 'f3.d': 'PC gamer, NAS, HTPC, nós de render. Pareie um PC clicando em Permitir nele, ou com um código de uso único. Ligue um PC desligado pelo painel.',
    'f4.t': 'Alertas no celular', 'f4.d': 'CPU ou GPU quente, disco com defeito, volume cheio, bateria fraca, PC sem resposta. Notificações do Windows e também ntfy, Discord ou Telegram. As regras rodam no serviço, então funcionam com o painel fechado.',
    'f5.t': 'FPS sobre o jogo', 'f5.d': 'FPS, 1 % low e tempo de quadro por cima de jogos em tela cheia, lidos dos eventos do próprio Windows. Nada é injetado no jogo.',
    'f6.t': 'Widget na área de trabalho', 'f6.d': 'Um widget que alterna entre seus PCs, ou um por PC. Vertical ou horizontal, compacto ou visual, com transparência ajustável e atalho global.',
    'f7.t': 'Feito para notebooks também', 'f7.d': 'Carga da bateria, consumo em watts, tempo restante, saúde e ciclos de carga, sinal do Wi-Fi, modo de energia e alertas de bateria fraca, carregada e desgastada.',
    'f8.t': 'Dois anos de histórico', 'f8.d': 'Todas as métricas gravadas; sessões de jogo com FPS, temperaturas e consumo; tempo ligado, energia e tráfego por dia; exportação em CSV.',
    'f9.t': 'Benchmarks', 'f9.d': 'Disco, rede entre dois PCs, CPU, memória, GPU (DirectX 11 compute) e um teste de estabilidade com verificação de erros e detecção de throttling.',
    'gallery.title': 'Por dentro',
    'cap.overlay': 'FPS sobre o jogo', 'cap.dashboard': 'Painel da máquina', 'cap.widgets': 'Widgets na área de trabalho',
    'cap.games': 'Histórico de jogos', 'cap.usage': 'Uso por dia', 'cap.bench': 'Benchmarks', 'cap.system': 'Resumo do sistema',
    'alt.overview': 'Visão geral do Nodekeeper com três PCs: workstation, NAS e HTPC',
    'alt.overlay': 'FPS sobre o jogo: FPS, 1 % low, tempo de quadro, GPU e CPU',
    'alt.dashboard': 'Painel da máquina com gráficos de 24 horas', 'alt.widgets': 'Um widget por PC na área de trabalho',
    'alt.games': 'Histórico de jogos com FPS, 1 % low e consumo', 'alt.usage': 'Uso por dia: tempo ligado, energia, temperaturas, rede e disco',
    'alt.bench': 'Benchmarks de disco e processador', 'alt.system': 'Resumo do sistema: Windows, placa-mãe, BIOS, CPU e memória',
    'alt.add': 'Adicionar máquina: encontre o PC e confira o certificado', 'alt.request': 'Pedido de acesso mostrado no outro PC',
    'install.title': 'Funcionando em um minuto',
    's1.t': 'Baixe e rode o instalador', 's1.d': 'Ele ainda não tem assinatura digital, então o SmartScreen do Windows avisa: clique em <b>Mais informações › Executar assim mesmo</b>.',
    's2.t': 'Escolha o papel deste PC', 's2.d': '<b>Completo</b> para o PC que você usa, <b>Só o serviço</b> para um PC acompanhado de outro (NAS, servidor, nó de render) ou <b>Só o painel</b>.',
    's3.t': 'Adicione os outros PCs', 's3.d': '<b>Adicionar máquina</b>, encontre pelo nome, <b>Pedir acesso</b> e clique em <b>Permitir</b> no outro PC. Um PC só com o serviço mostra um código de pareamento.',
    'trust.title': 'O que ele instala, e por quê',
    'trust.sub': 'Sem surpresas. Isto é tudo o que o instalador muda no seu sistema.',
    't1.t': 'Um serviço do Windows, como administrador', 't1.d': 'Ler os sensores da placa-mãe, da CPU e dos discos (e os eventos de quadro do FPS) exige direitos de administrador. O serviço <b>Nodekeeper</b> faz esse trabalho, e o painel roda como usuário comum.',
    't2.t': 'O driver PawnIO', 't2.d': 'Temperatura e consumo da CPU, ventoinhas e temperatura da RAM são lidos pelo <a href="https://pawnio.eu">PawnIO</a>, baixado do projeto oficial se ainda não estiver instalado. Outros programas de sensores usam o mesmo driver, por isso ele fica ao desinstalar.',
    't3.t': 'Uma regra de firewall, só em redes privadas', 't3.d': 'TCP 47821 em redes privadas e de domínio, nunca em Wi-Fi público, para os painéis dos outros PCs alcançarem o serviço. O tráfego é HTTPS, com um certificado por máquina fixado no pareamento.',
    't4.t': 'Seus dados ficam em casa', 't4.d': 'Sem telemetria e sem contas. O serviço só responde aos painéis pareados na sua rede e nunca acessa a internet. O painel procura atualizações no GitHub uma vez por dia, e isso pode ser desligado.',
    'sign.t': 'Ainda sem assinatura digital, e como conferir mesmo assim',
    'sign.d': 'O Nodekeeper é um projeto de uma pessoa só, e a assinatura gratuita para código aberto pede antes um histórico público. Enquanto isso, toda versão é gerada pelo GitHub Actions a partir do código marcado, nada é gerado na máquina do desenvolvedor, e cada versão traz o link da análise do instalador no VirusTotal. Se o Controle Inteligente de Aplicativos estiver ligado, o Windows bloqueia programas sem assinatura; nesse caso, vale esperar a versão assinada.',
    'sign.vt': 'Notas da versão e análise no VirusTotal', 'sign.policy': 'Política de assinatura', 'sign.privacy': 'Política de privacidade',
    'final.title': 'Gratuito, e continua gratuito',
    'final.sub': 'O Nodekeeper tem licença MIT. Se ele for útil para você, uma estrela no GitHub ou um relato de problema ajuda o projeto a crescer.',
    'final.star': '★ Estrela no GitHub', 'final.issue': 'Relatar um problema',
    'foot.mit': 'licença MIT', 'foot.privacy': 'Privacidade', 'foot.notices': 'Componentes de terceiros',
  };

  const en = new Map();
  let lang = 'en';
  let release = null; // { version, size } da versão mais recente

  function store(key, value) {
    try { if (value === undefined) return localStorage.getItem(key); localStorage.setItem(key, value); } catch { return null; }
  }

  function apply(next) {
    lang = next;
    document.documentElement.lang = next === 'pt' ? 'pt-BR' : 'en';
    for (const el of document.querySelectorAll('[data-i18n]')) {
      const k = el.dataset.i18n;
      if (!en.has(k)) en.set(k, el.innerHTML);
      el.innerHTML = next === 'pt' && PT[k] ? PT[k] : en.get(k);
    }
    for (const el of document.querySelectorAll('[data-i18n-alt]')) {
      const k = el.dataset.i18nAlt, id = 'alt:' + k;
      if (!en.has(id)) en.set(id, el.alt);
      el.alt = next === 'pt' && PT[k] ? PT[k] : en.get(id);
    }
    for (const el of document.querySelectorAll('[data-i18n-aria]')) {
      const k = el.dataset.i18nAria, id = 'aria:' + k;
      if (!en.has(id)) en.set(id, el.getAttribute('aria-label'));
      el.setAttribute('aria-label', next === 'pt' && PT[k] ? PT[k] : en.get(id));
    }
    const btn = document.getElementById('lang');
    btn.textContent = next === 'pt' ? 'EN' : 'PT';
    btn.setAttribute('aria-label', next === 'pt' ? 'English' : 'Português');
    showVersion();
  }

  document.getElementById('lang').addEventListener('click', () => {
    const next = lang === 'pt' ? 'en' : 'pt';
    store('nk-lang', next);
    apply(next);
  });

  // Inglês por padrão; português só quando o visitante escolhe no botão PT (a escolha fica guardada).
  apply(store('nk-lang') === 'pt' ? 'pt' : 'en');

  // ---------- Versão mais recente: link direto para o instalador ----------
  function showVersion() {
    if (!release) return;
    const el = document.getElementById('version');
    const mb = release.size ? ` · ${Math.round(release.size / 1048576)} MB` : '';
    el.textContent = (lang === 'pt' ? 'Versão ' : 'Version ') + release.version + mb;
  }

  // /releases/latest ignora as pré-versões (todas são, por enquanto): pega a mais nova publicada da lista.
  fetch(`https://api.github.com/repos/${REPO}/releases?per_page=10`, { headers: { Accept: 'application/vnd.github+json' } })
    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
    .then(list => {
      const data = list.find(r => !r.draft && (r.assets || []).some(a => /\.exe$/i.test(a.name)));
      if (!data) return;
      for (const a of document.querySelectorAll('a.notes')) a.href = data.html_url;
      const exe = (data.assets || []).find(a => /^NodekeeperSetup-.*\.exe$/i.test(a.name));
      release = { version: String(data.tag_name || '').replace(/^v/, ''), size: exe && exe.size };
      if (exe) for (const a of document.querySelectorAll('a.dl')) a.href = exe.browser_download_url;
      showVersion();
    })
    .catch(() => { /* sem rede ou limite da API: os botões continuam indo para a página da versão */ });

  // ---------- Vídeo: só carrega ao clicar ----------
  const player = document.getElementById('player');
  player.querySelector('.video-facade').addEventListener('click', () => {
    const id = player.dataset.youtube;
    if (id) {
      const f = document.createElement('iframe');
      f.src = `https://www.youtube-nocookie.com/embed/${encodeURIComponent(id)}?autoplay=1&rel=0&modestbranding=1`;
      f.title = 'Nodekeeper';
      f.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen';
      f.allowFullscreen = true;
      player.replaceChildren(f);
    } else {
      const v = document.createElement('video');
      v.src = 'media/nodekeeper-promo.mp4';
      v.controls = true; v.autoplay = true; v.playsInline = true;
      player.replaceChildren(v);
    }
  });

  // ---------- Ampliar capturas ----------
  const box = document.getElementById('lightbox');
  const big = box.querySelector('img');
  for (const img of document.querySelectorAll('.shot img')) {
    img.addEventListener('click', () => { big.src = img.src; big.alt = img.alt; box.hidden = false; });
  }
  box.addEventListener('click', () => { box.hidden = true; });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') box.hidden = true; });
})();
