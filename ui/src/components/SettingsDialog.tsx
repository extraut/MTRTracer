import { useEffect, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { request } from '../bridge/client';
import { defaultUrlTemplate, type CacheStats, type GeoDbAvailability, type GeoDbRelease, type GeoProviders } from '../bridge/protocol';
import { L, locale } from '../i18n';
import { useStore, type SettingsTab } from '../store';
import { themes, type ThemeId } from '../theme';
import { Close, Download, External, Folder, Restart, Trash } from './Icons';
import './settings.css';

const tabs: { id: SettingsTab; label: () => string }[] = [
  { id: 'general', label: () => L('Общие', 'General') },
  { id: 'geo', label: () => L('Офлайн-база', 'Offline database') },
  { id: 'sources', label: () => L('Источники', 'Sources') },
  { id: 'updates', label: () => L('Обновления', 'Updates') },
];

export function SettingsDialog() {
  const tab = useStore((s) => s.settingsTab);
  const open = useStore((s) => s.openSettings);
  useEffect(() => {
    if (!tab) return;
    const k = (e: KeyboardEvent) => e.key === 'Escape' && open(null);
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, [tab, open]);
  return (
    <AnimatePresence>
      {tab && (
        <motion.div
          className="gate settings-overlay"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.18 }}
          onMouseDown={(e) => e.target === e.currentTarget && open(null)}
        >
          <motion.section
            className="gate-panel settings-panel"
            role="dialog"
            aria-modal="true"
            aria-labelledby="settings-title"
            initial={{ opacity: 0, y: 10, scale: 0.985 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 6, scale: 0.99 }}
            transition={{ duration: 0.26, ease: [0.16, 1, 0.3, 1] }}
          >
            <header className="settings-head">
              <h1 id="settings-title">{L('Настройки и данные', 'Settings and data')}</h1>
              <button className="btn ghost icon" onClick={() => open(null)} title={L('Закрыть (Esc)', 'Close (Esc)')} aria-label={L('Закрыть', 'Close')}>
                <Close size={15} />
              </button>
            </header>
            <div className="settings-body">
              <nav className="settings-nav" role="tablist" aria-orientation="vertical">
                {tabs.map((t) => (
                  <button key={t.id} role="tab" aria-selected={tab === t.id} className={tab === t.id ? 'on' : ''} onClick={() => open(t.id)}>
                    {t.label()}
                  </button>
                ))}
              </nav>
              <div className="settings-content" role="tabpanel">
                {tab === 'general' && <GeneralTab />}
                {tab === 'geo' && <GeoTab />}
                {tab === 'sources' && <SourcesTab />}
                {tab === 'updates' && <UpdatesTab />}
              </div>
            </div>
          </motion.section>
        </motion.div>
      )}
    </AnimatePresence>
  );
}

// ---------------- shared bits ----------------

function Section({ title, hint, children, actions }: { title: string; hint?: React.ReactNode; children?: React.ReactNode; actions?: React.ReactNode }) {
  return (
    <section className="st-section">
      <div className="st-section-head">
        <div>
          <h2>{title}</h2>
          {hint && <p className="st-hint">{hint}</p>}
        </div>
        {actions && <div className="st-actions">{actions}</div>}
      </div>
      {children}
    </section>
  );
}

function Switch({ label, hint, on, onChange, badge, disabled }: { label: string; hint?: React.ReactNode; on: boolean; onChange: (v: boolean) => void; badge?: React.ReactNode; disabled?: boolean }) {
  return (
    <button className="toggle st-toggle" onClick={() => onChange(!on)} role="switch" aria-checked={on} disabled={disabled}>
      <span className="toggle-text">
        <span>
          {label} {badge}
        </span>
        {hint && <span className="toggle-hint">{hint}</span>}
      </span>
      <span className={`switch ${on ? 'on' : ''}`} />
    </button>
  );
}

function ExtLink({ url, children }: { url: string; children: React.ReactNode }) {
  const notify = useStore((s) => s.notify);
  return (
    <a
      className="st-link"
      href={url}
      onClick={(e) => {
        e.preventDefault();
        request('shell.url', { url }).catch((err) => notify((err as Error).message, 'error'));
      }}
    >
      {children}
      <External size={11} />
    </a>
  );
}

const mb = (bytes: number | null | undefined) => (bytes == null ? '—' : `${(bytes / 1048576).toFixed(bytes > 10 * 1048576 ? 0 : 1)} ${L('МБ', 'MB')}`);

// ---------------- general ----------------

const themeInfo: Record<ThemeId, { name: () => string; colors: [string, string, string, string, string] }> = {
  system: { name: () => L('Как в системе', 'System'), colors: ['#f7f8fa', '#17181c', '#5154c9', '#9b9cf0', '#57d29a'] },
  dark: { name: () => L('Графит', 'Graphite'), colors: ['#17181c', '#1d1e23', '#9b9cf0', '#57d29a', '#ec5d55'] },
  light: { name: () => L('Светлая', 'Light'), colors: ['#f9fafb', '#ffffff', '#4f52c4', '#2f9e6b', '#c9423b'] },
  mocha: { name: () => 'Catppuccin Mocha', colors: ['#1e1e2e', '#24243a', '#cba6f7', '#a6e3a1', '#f38ba8'] },
  nord: { name: () => 'Nord', colors: ['#2e3440', '#343b48', '#88c0d0', '#a3be8c', '#bf616a'] },
};

function GeneralTab() {
  const lang = useStore((s) => s.lang);
  const setLang = useStore((s) => s.setLang);
  const theme = useStore((s) => s.theme);
  const setTheme = useStore((s) => s.setTheme);
  const info = useStore((s) => s.info);
  return (
    <>
      <Section title={L('Язык', 'Language')} hint={L('Интерфейс, разбор маршрута, события и отчёты.', 'Interface, route analysis, events and reports.')}>
        <div className="seg st-seg">
          <button className={lang === 'ru' ? 'on' : ''} onClick={() => void setLang('ru')}>
            Русский
          </button>
          <button className={lang === 'en' ? 'on' : ''} onClick={() => void setLang('en')}>
            English
          </button>
        </div>
      </Section>
      <Section title={L('Тема', 'Theme')}>
        <div className="theme-grid">
          {themes.map((t) => {
            const ti = themeInfo[t];
            return (
              <button key={t} className={`theme-card ${theme === t ? 'on' : ''}`} onClick={() => setTheme(t)} aria-pressed={theme === t}>
                <span className={`theme-preview ${t === 'system' ? 'split' : ''}`} style={{ background: ti.colors[0] }}>
                  {t === 'system' && <span className="theme-half" style={{ background: ti.colors[1] }} />}
                  <span className="theme-bar" style={{ background: t === 'system' ? 'transparent' : ti.colors[1] }}>
                    <i style={{ background: ti.colors[2] }} />
                  </span>
                  <span className="theme-dots">
                    <i style={{ background: ti.colors[2] }} />
                    <i style={{ background: ti.colors[3] }} />
                    <i style={{ background: ti.colors[4] }} />
                  </span>
                </span>
                <span className="theme-name">{ti.name()}</span>
              </button>
            );
          })}
        </div>
      </Section>
      <Section
        title={L('Данные программы', 'App data')}
        hint={L('Настройки, история замеров, базы и кэши геоданных, логи сессий.', 'Settings, measurement history, geo databases and caches, session logs.')}
        actions={
          <button className="btn sm" onClick={() => void request('shell.open', { path: 'data' })}>
            <Folder size={13} /> {L('Открыть папку', 'Open folder')}
          </button>
        }
      >
        {info?.dataDir && <code className="st-path selectable">{info.dataDir}</code>}
      </Section>
    </>
  );
}

// ---------------- offline database ----------------

function GeoTab() {
  const geo = useStore((s) => s.geoStatus);
  const providers = useStore((s) => s.providers);
  const setProviders = useStore((s) => s.setProviders);
  const geoDb = useStore((s) => s.geoDb);
  const setGeoDb = useStore((s) => s.setGeoDb);
  const notify = useStore((s) => s.notify);
  const hostKind = useStore((s) => s.info?.platform);
  const [releases, setReleases] = useState<GeoDbRelease[] | null>(null);
  const [available, setAvailable] = useState<GeoDbAvailability[] | null>(null);
  const [checking, setChecking] = useState(false);
  const [path, setPath] = useState('');
  const [url, setUrl] = useState('');
  const [template, setTemplate] = useState(geoDb.urlTemplate);
  const [busy, setBusy] = useState(false);

  const busyDb = geo?.db === 'downloading' || geo?.db === 'updating';
  const reload = () =>
    request('geo.db.list')
      .then((r) => setReleases(r.releases))
      .catch((e) => notify((e as Error).message, 'error'));
  useEffect(() => {
    void reload();
    // Refresh the list whenever a download or import finishes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [geo?.db, geo?.dbDate, geo?.active]);

  const run = async <T,>(p: Promise<T>, ok?: string) => {
    setBusy(true);
    try {
      await p;
      if (ok) notify(ok);
      await reload();
    } catch (e) {
      notify((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  const check = async () => {
    setChecking(true);
    try {
      setAvailable(await request('geo.db.available', { count: 6 }));
    } catch (e) {
      notify((e as Error).message, 'error');
    } finally {
      setChecking(false);
    }
  };

  const pick = async () => {
    try {
      const r = await request('file.pick');
      if (r.path) {
        setPath(r.path);
        await run(request('geo.db.import', { path: r.path }), L('База импортирована и включена', 'Database imported and activated'));
      }
    } catch (e) {
      notify((e as Error).message, 'error');
    }
  };

  const autoActive = geoDb.active == null;
  return (
    <>
      <Section
        title={L('Офлайн-база геоданных', 'Offline geo database')}
        hint={L(
          'Город и сеть по IP без интернета. По умолчанию DB-IP Lite (CC BY 4.0), выходит раз в месяц. Можно держать несколько выпусков и выбрать любой, или подключить свою базу в формате MaxMind.',
          'City and network by IP, offline. DB-IP Lite (CC BY 4.0) by default, released monthly. Keep several releases and pick any of them, or plug in your own MaxMind-format database.',
        )}
      >
        <div className="st-status">
          <span className={`st-dot ${geo?.db ?? 'missing'}`} />
          <span>
            {geo?.enabled === false
              ? L('Выключена', 'Turned off')
              : busyDb
                ? `${L('Загрузка', 'Downloading')} ${geo?.stage ?? ''} · ${Math.round((geo?.progress ?? 0) * 100)}%`
                : geo?.db === 'ready'
                  ? `${L('Используется', 'In use')}: ${geo.active === 'custom' ? L('свой файл', 'custom file') + ` (${geo.dbDate})` : `DB-IP Lite ${geo.dbDate}`}`
                  : geo?.db === 'error'
                    ? `${L('Ошибка', 'Error')}: ${geo.error ?? ''}`
                    : L('Не загружена', 'Not downloaded')}
          </span>
          {busyDb && (
            <span className="st-progress">
              <i style={{ width: `${Math.round((geo?.progress ?? 0) * 100)}%` }} />
            </span>
          )}
        </div>
        {geo?.error && geo.db !== 'error' && <p className="st-error">{geo.error}</p>}
        <Switch
          label={L('Использовать офлайн-базу', 'Use the offline database')}
          hint={L('Если выключить, места узлов будут только из онлайн-источников и имён роутеров.', 'When off, router locations come only from online sources and router names.')}
          on={providers.offlineDb}
          onChange={(v) => void setProviders({ offlineDb: v })}
        />
        <Switch
          label={L('Обновлять автоматически', 'Update automatically')}
          hint={L('При запуске проверять новый выпуск DB-IP (раз в месяц) и скачивать его. Не действует, если выбран конкретный выпуск или свой файл.', 'At launch, check for a new DB-IP release (monthly) and download it. Does not apply when a specific release or a custom file is selected.')}
          on={geoDb.autoUpdate}
          onChange={(v) => void setGeoDb({ autoUpdate: v })}
        />
      </Section>

      <Section
        title={L('Установленные базы', 'Installed databases')}
        actions={
          <button className="btn sm" disabled={busyDb} onClick={() => void run(request('geo.download', {}), L('Загрузка самого нового выпуска началась', 'Downloading the newest release'))}>
            <Download size={13} /> {L('Скачать новейшую', 'Download newest')}
          </button>
        }
      >
        <div className="db-list">
          <div className={`db-row ${autoActive ? 'active' : ''}`}>
            <div className="db-main">
              <b>{L('Самая новая из установленных', 'Newest installed')}</b>
              <span className="db-meta">{L('переключается сама после обновления', 'switches automatically after an update')}</span>
            </div>
            {autoActive ? (
              <span className="db-badge">{L('выбрано', 'selected')}</span>
            ) : (
              <button className="btn sm" disabled={busy} onClick={() => void run(request('geo.db.use', { id: null }))}>
                {L('Выбрать', 'Select')}
              </button>
            )}
          </div>
          {releases?.length === 0 && <p className="st-empty">{L('Пока ни одной базы. Нажмите «Скачать новейшую» или подключите свой файл.', 'No databases yet. Press "Download newest" or plug in your own file.')}</p>}
          {releases?.map((r) => (
            <div key={r.id} className={`db-row ${r.active ? 'live' : ''} ${geoDb.active === r.id ? 'active' : ''}`}>
              <div className="db-main">
                <b>{r.label}</b>
                <span className="db-meta">
                  {[r.type, r.built && r.id === 'custom' ? `${L('собрана', 'built')} ${r.built}` : null, r.city ? L('города', 'cities') : null, r.asn ? 'ASN' : null, mb(r.bytes)].filter(Boolean).join(' · ')}
                </span>
              </div>
              {r.active && <span className="db-badge live">{L('используется', 'in use')}</span>}
              {geoDb.active === r.id ? (
                <span className="db-badge">{L('закреплено', 'pinned')}</span>
              ) : (
                <button className="btn sm" disabled={busy || !r.city} onClick={() => void run(request('geo.db.use', { id: r.id }))} title={L('Использовать этот выпуск и не обновлять автоматически', 'Use this release and stop auto-updates')}>
                  {L('Закрепить', 'Pin')}
                </button>
              )}
              <button className="btn ghost icon sm" disabled={busy || busyDb} onClick={() => void run(request('geo.db.delete', { id: r.id }), L('Удалено', 'Deleted'))} title={L('Удалить', 'Delete')} aria-label={L('Удалить', 'Delete')}>
                <Trash size={14} />
              </button>
            </div>
          ))}
        </div>
      </Section>

      <Section
        title={L('Выпуски на сервере DB-IP', 'Releases on the DB-IP server')}
        hint={L('Проверка отправляет HEAD-запросы к серверу загрузки; сами файлы не скачиваются.', 'Checking sends HEAD requests to the download server; the files are not downloaded.')}
        actions={
          <button className="btn sm" disabled={checking} onClick={() => void check()}>
            <Restart size={13} className={checking ? 'spin' : ''} /> {L('Проверить', 'Check')}
          </button>
        }
      >
        {available && (
          <div className="db-list">
            {available.map((a) => (
              <div key={a.month} className="db-row">
                <div className="db-main">
                  <b>DB-IP Lite {a.month}</b>
                  <span className="db-meta">{a.available ? `${L('доступен', 'available')} · ${L('город', 'city')} ${mb(a.bytes)} (gzip)` : L('нет на сервере', 'not on the server')}</span>
                </div>
                {a.installed ? (
                  <span className="db-badge">{L('установлен', 'installed')}</span>
                ) : (
                  <button className="btn sm" disabled={!a.available || busyDb} onClick={() => void run(request('geo.download', { month: a.month }), L('Загрузка началась', 'Download started'))}>
                    <Download size={13} /> {L('Скачать', 'Download')}
                  </button>
                )}
              </div>
            ))}
          </div>
        )}
      </Section>

      <Section
        title={L('Своя база', 'Your own database')}
        hint={
          <>
            {L('Формат MaxMind: DB-IP Lite City/ASN, MaxMind GeoLite2 City/ASN (нужна бесплатная регистрация), IPinfo ASN. Файлы .mmdb, .mmdb.gz или .tar.gz.', 'MaxMind format: DB-IP Lite City/ASN, MaxMind GeoLite2 City/ASN (free sign-up required), IPinfo ASN. Files .mmdb, .mmdb.gz or .tar.gz.')}{' '}
            <ExtLink url="https://db-ip.com/db/lite.php">DB-IP Lite</ExtLink> · <ExtLink url="https://dev.maxmind.com/geoip/geolite2-free-geolocation-data">GeoLite2</ExtLink>
          </>
        }
      >
        <div className="st-row">
          <input className="field mono" value={path} spellCheck={false} placeholder={L('Путь к файлу .mmdb', 'Path to an .mmdb file')} onChange={(e) => setPath(e.target.value)} />
          <button className="btn sm" disabled={busy} onClick={() => void pick()} hidden={hostKind === undefined}>
            <Folder size={13} /> {L('Выбрать…', 'Browse…')}
          </button>
          <button className="btn sm primary" disabled={busy || !path.trim()} onClick={() => void run(request('geo.db.import', { path: path.trim() }), L('База импортирована и включена', 'Database imported and activated'))}>
            {L('Импорт', 'Import')}
          </button>
        </div>
        <div className="st-row">
          <input className="field mono" value={url} spellCheck={false} placeholder="https://…/GeoLite2-City.tar.gz" onChange={(e) => setUrl(e.target.value)} />
          <button className="btn sm primary" disabled={busy || busyDb || !/^https:\/\//i.test(url.trim())} onClick={() => void run(request('geo.db.fetch', { url: url.trim() }), L('База скачана и включена', 'Database downloaded and activated'))}>
            <Download size={13} /> {L('Скачать по ссылке', 'Download from link')}
          </button>
        </div>
      </Section>

      <Section
        title={L('Адрес выпусков DB-IP', 'DB-IP release address')}
        hint={L('Если DB-IP поменяет адрес загрузки, его можно исправить здесь. {name} и {month} подставляются автоматически.', 'If DB-IP changes its download address, fix it here. {name} and {month} are filled in automatically.')}
      >
        <div className="st-row">
          <input className="field mono" value={template} spellCheck={false} onChange={(e) => setTemplate(e.target.value)} />
          <button className="btn sm" disabled={template === geoDb.urlTemplate} onClick={() => void setGeoDb({ urlTemplate: template }).then(() => notify(L('Сохранено', 'Saved')))}>
            {L('Сохранить', 'Save')}
          </button>
          <button
            className="btn sm ghost"
            disabled={geoDb.urlTemplate === defaultUrlTemplate && template === defaultUrlTemplate}
            onClick={() => {
              setTemplate(defaultUrlTemplate);
              void setGeoDb({ urlTemplate: defaultUrlTemplate });
            }}
          >
            {L('По умолчанию', 'Default')}
          </button>
        </div>
      </Section>
    </>
  );
}

// ---------------- sources ----------------

const sourceList: { key: keyof GeoProviders; name: string; online: boolean; what: () => string; sends: () => string; url?: string }[] = [
  { key: 'hostnameHints', name: 'Router names', online: false, what: () => L('Коды городов в обратном DNS роутеров (fra, ams, waw…).', 'City codes in router reverse DNS (fra, ams, waw…).'), sends: () => L('ничего', 'nothing') },
  { key: 'ripeIpmap', name: 'RIPE IPmap', online: true, url: 'https://ipmap.ripe.net', what: () => L('Место по реальным замерам задержек RIPE Atlas, сделано для роутеров.', 'Location from real RIPE Atlas latency measurements, made for routers.'), sends: () => L('адреса узлов маршрута', 'route router addresses') },
  { key: 'ripeStat', name: 'RIPEstat', online: true, url: 'https://stat.ripe.net', what: () => L('MaxMind GeoLite (обновляется еженедельно) и живые BGP-данные: префикс и AS.', 'MaxMind GeoLite (updated weekly) and live BGP data: prefix and AS.'), sends: () => L('адреса узлов маршрута', 'route router addresses') },
  { key: 'ipApi', name: 'ip-api.com', online: true, url: 'https://ip-api.com', what: () => L('Названия провайдера и организации, запасное место.', 'ISP and organisation names, fallback location.'), sends: () => L('адреса узлов маршрута', 'route router addresses') },
  { key: 'cloudflareSelf', name: 'Cloudflare meta', online: true, url: 'https://speed.cloudflare.com', what: () => L('Ваш публичный IP и место — точка отсчёта карты.', 'Your public IP and location — the starting point of the map.'), sends: () => L('запрос с вашего реального IP (физический адаптер)', 'a request from your real IP (physical adapter)') },
  { key: 'cloudflareColo', name: 'Cloudflare colo', online: true, what: () => L('Какой дата-центр Cloudflare отвечает для сайта за Cloudflare.', 'Which Cloudflare data centre answers for a site behind Cloudflare.'), sends: () => L('запрос /cdn-cgi/trace к самому сайту', 'a /cdn-cgi/trace request to the site itself') },
  { key: 'teamCymru', name: 'Team Cymru', online: true, what: () => L('Номер AS через DNS, если других данных нет.', 'AS number over DNS when nothing else has it.'), sends: () => L('DNS-запрос с адресом узла', 'a DNS query containing the router address') },
];

function SourcesTab() {
  const providers = useStore((s) => s.providers);
  const setProviders = useStore((s) => s.setProviders);
  const online = useStore((s) => s.options.geoOnline);
  const setOptions = useStore((s) => s.setOptions);
  const notify = useStore((s) => s.notify);
  const [stats, setStats] = useState<CacheStats | null>(null);
  useEffect(() => {
    request('geo.cache.stats').then(setStats).catch(() => undefined);
  }, []);
  const cached = stats ? stats.ripeIpmap + stats.ripeStatGeo + stats.ripeStatNet + stats.ipApi : null;
  return (
    <>
      <Section
        title={L('Онлайн-уточнение', 'Online refinement')}
        hint={L('Главный выключатель всех онлайн-источников ниже. Офлайн-база и имена роутеров работают и без него.', 'Master switch for all online sources below. The offline database and router names work without it.')}
      >
        <Switch label={L('Разрешить онлайн-источники', 'Allow online sources')} on={online} onChange={(v) => setOptions({ geoOnline: v })} />
      </Section>
      <Section title={L('Источники геоданных', 'Geo data sources')} hint={L('Выключенный источник больше не получает запросов и не участвует в выборе места.', 'A disabled source gets no more requests and takes no part in choosing locations.')}>
        <div className="src-list">
          {sourceList.map((s) => (
            <Switch
              key={s.key}
              label={s.key === 'hostnameHints' ? L('Имена роутеров', 'Router names') : s.name}
              badge={<span className={`src-badge ${s.online ? 'online' : ''}`}>{s.online ? L('онлайн', 'online') : L('офлайн', 'offline')}</span>}
              hint={
                <>
                  {s.what()} <span className="src-sends">{L('Отправляется', 'Sends')}: {s.sends()}.</span>
                </>
              }
              on={providers[s.key]}
              disabled={s.online && !online}
              onChange={(v) => void setProviders({ [s.key]: v })}
            />
          ))}
        </div>
      </Section>
      <Section
        title={L('Кэш ответов', 'Response cache')}
        hint={L('Ответы онлайн-источников хранятся от 2 до 30 дней, чтобы не спрашивать одно и то же. После очистки адреса текущего маршрута запрашиваются заново.', 'Online answers are kept for 2 to 30 days so the same thing is not asked twice. After clearing, the current route is looked up again.')}
        actions={
          <button
            className="btn sm"
            onClick={() =>
              void request('geo.cache.clear')
                .then((s) => {
                  setStats(s);
                  notify(L('Кэш очищен', 'Cache cleared'));
                })
                .catch((e) => notify((e as Error).message, 'error'))
            }
          >
            <Trash size={13} /> {L('Очистить', 'Clear')}
          </button>
        }
      >
        {cached != null && (
          <p className="st-meta">
            {L('В кэше', 'Cached')}: {cached.toLocaleString(locale())} {L('ответов', 'answers')}
          </p>
        )}
      </Section>
    </>
  );
}

// ---------------- updates ----------------

function UpdatesTab() {
  const info = useStore((s) => s.info);
  const update = useStore((s) => s.update);
  const checking = useStore((s) => s.updateChecking);
  const check = useStore((s) => s.checkUpdate);
  const repo = useStore((s) => s.updateRepo);
  const setRepo = useStore((s) => s.setUpdateRepo);
  const notify = useStore((s) => s.notify);
  const [repoText, setRepoText] = useState(repo);
  useEffect(() => setRepoText(repo), [repo]);
  const platform = info?.platform;
  const mine = update?.assets.filter((a) => (platform === 'linux' ? /linux/i.test(a.name) : platform === 'windows' ? /windows|\.exe$/i.test(a.name) : true)) ?? [];
  return (
    <>
      <Section
        title={`MTRTracer ${info?.version ?? ''}`}
        hint={L('Проверка спрашивает у GitHub последний релиз. Программа сама ничего не скачивает и не заменяет: файл открывается в браузере.', 'The check asks GitHub for the latest release. The app never downloads or replaces itself: the file opens in your browser.')}
        actions={
          <button className="btn sm primary" disabled={checking} onClick={() => void check()}>
            <Restart size={13} className={checking ? 'spin' : ''} /> {L('Проверить обновления', 'Check for updates')}
          </button>
        }
      >
        {update && (
          <div className={`upd-card ${update.newerAvailable ? 'new' : ''}`}>
            {update.error ? (
              <p className="st-error">{update.error}</p>
            ) : update.newerAvailable ? (
              <p>
                <b>{L('Доступна версия', 'Version available')} {update.latest}</b>
                {update.publishedAt && <span className="st-meta"> · {new Date(update.publishedAt).toLocaleDateString(locale())}</span>}
              </p>
            ) : (
              <p>
                {L('Установлена последняя версия', 'You have the latest version')} ({update.latest ?? update.current})
              </p>
            )}
            {update.newerAvailable && mine.length > 0 && (
              <div className="upd-assets">
                {mine.map((a) => (
                  <button key={a.name} className="btn sm" onClick={() => void request('shell.url', { url: a.url }).catch((e) => notify((e as Error).message, 'error'))}>
                    <Download size={13} /> {a.name} <span className="st-meta">{mb(a.size)}</span>
                  </button>
                ))}
              </div>
            )}
            {update.notes && update.newerAvailable && <pre className="upd-notes selectable">{update.notes}</pre>}
            {update.url && <ExtLink url={update.url}>{L('Страница релиза', 'Release page')}</ExtLink>}
          </div>
        )}
      </Section>

      <Section
        title={L('Публичный архив', 'Public archive')}
        hint={L(
          'Проект выложен как открытый архив: автор не обещает регулярных обновлений. Программа остаётся рабочей — базы и источники обновляются отсюда, без новой версии. Хотите развивать дальше — сделайте форк, лицензия MIT это разрешает.',
          'The project is published as an open archive: the author does not promise regular updates. The app keeps working — databases and sources are updated from here, without a new version. Want to take it further? Fork it; the MIT licence allows that.',
        )}
      >
        <div className="st-links">
          <ExtLink url={`https://github.com/${repo}`}>{L('Репозиторий', 'Repository')}</ExtLink>
          <ExtLink url={`https://github.com/${repo}/releases`}>{L('Все релизы', 'All releases')}</ExtLink>
          <ExtLink url={`https://github.com/${repo}/forks`}>{L('Форки', 'Forks')}</ExtLink>
          <ExtLink url="https://extraut.github.io/MTRTracer/">{L('Сайт и документация', 'Website and docs')}</ExtLink>
        </div>
      </Section>

      <Section
        title={L('Откуда брать обновления', 'Where updates come from')}
        hint={L('Репозиторий GitHub в виде владелец/имя. Если пользуетесь форком, укажите его — проверка будет смотреть его релизы.', 'GitHub repository as owner/name. If you use a fork, put it here and the check will look at its releases.')}
      >
        <div className="st-row">
          <input className="field mono" value={repoText} spellCheck={false} onChange={(e) => setRepoText(e.target.value)} placeholder="owner/MTRTracer" />
          <button className="btn sm" disabled={repoText.trim() === repo} onClick={() => void setRepo(repoText.trim()).then(() => notify(L('Сохранено', 'Saved')))}>
            {L('Сохранить', 'Save')}
          </button>
          <button className="btn sm ghost" disabled={repo === 'extraut/MTRTracer'} onClick={() => void setRepo('extraut/MTRTracer')}>
            {L('Исходный', 'Original')}
          </button>
        </div>
      </Section>
    </>
  );
}
