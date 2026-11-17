const en = {
  lang: 'en',
  prefix: '',
  title: "Promin: show one PC's screen on every PC in the room",
  description:
    "A free, open-source app for Windows 11 that shows one PC's screen on every other PC over the local network, with no server and no internet.",
  otherLanguage: { lang: 'uk', prefix: 'ua/', label: 'Українська' },
  nav: { features: 'Features', docs: 'Docs', download: 'Download' },
  hero: {
    title: 'Show your screen on every PC in the room.',
    lead: "Promin is a free app for Windows 11. It puts one PC's screen on all the others over the local network. No server, no account, no internet connection.",
    download: 'Download',
    source: 'Source code on GitHub',
    roomLabel: 'A room of twelve PCs. A beam sweeps across them and each one lights up with the same screen.',
  },
  features: {
    title: 'The whole room, from your PC.',
    more: 'More about each feature',
    screenshotAlt:
      'The Promin console: a tile for each PC in the room, with Lock, Send files and Start broadcast along the bottom.',
    items: [
      {
        id: 'broadcast',
        name: 'Broadcast',
        text: 'Your screen covers every other display, and their keyboards and mice wait until you stop.',
        details: [
          'The console lets you choose the picture quality and its sharpness.',
          'A PC that drops off the network rejoins the broadcast by itself when it is back.',
          'You can stop sending to one PC without stopping the rest.',
          'The picture travels as H.264 video, encoded and decoded on the graphics card where there is one.',
        ],
      },
      {
        id: 'screens',
        name: 'See every screen',
        text: 'Each PC has a tile with a picture of its screen, refreshed about once a second.',
        details: [
          'The pictures keep coming during a broadcast too.',
          'Each tile says whether its PC is ready, receiving, reconnecting, locked or offline.',
          'Tick PCs on their tiles to pick which ones to lock, send files to, shut down or restart.',
        ],
      },
      {
        id: 'lock',
        name: 'Lock',
        text: 'Lock one PC, a few or all of them, with a message of up to 100 characters. The lock holds through a restart, and one click lifts it.',
        details: [
          'The lock is kept on the PC itself, so it survives a restart, a sign-out and the console being closed. It comes back a moment after sign-in.',
          'Only your console can unlock the PC.',
          'The PC the console runs on is never locked, and a broadcast does not block its keyboard and mouse.',
        ],
      },
      {
        id: 'files',
        name: 'Send files',
        text: 'Send files and folders to the ticked PCs, and open them there as soon as they arrive.',
        details: [
          'They go to a Promin folder on the desktop of whoever is signed in.',
          'Tick Open them on each PC when they arrive, and they open in their usual program. Programs are never run.',
          'Each tile shows how far its send has got, and a send that failed can be sent again to just that PC.',
        ],
      },
      {
        id: 'control',
        name: 'Control a PC',
        text: 'Work on another PC full screen, with your own keyboard and mouse. Copy and paste goes both ways.',
        details: [
          'Double-click a tile, or open its ⋯ panel and choose Control this PC.',
          'The picture fills your screen, with a bar that tucks away. The bar can also move the picture to a separate window.',
          'Copy and paste works for text, up to 10,000 characters at a time.',
          'The PC shows a tab reading “This PC is being controlled remotely”, and its own keyboard and mouse are blocked.',
          "One PC at a time, not during a broadcast, and never the console's own PC. Controlling a locked PC unlocks it.",
          'The Windows lock screen, administrator prompts and the sign-in screen cannot be reached yet.',
        ],
      },
      {
        id: 'power',
        name: 'Shut down and restart',
        text: 'Turn off or restart the ticked PCs at the end of the day, after one confirmation.',
        details: [
          'It happens at once: every app on them closes without saving.',
          "The console's own PC is never shut down or restarted.",
        ],
      },
    ],
  },
  featuresPage: {
    title: 'What Promin does.',
    lead: 'Two apps: the console on your PC, and the agent on every other PC in the room. Everything on this page happens from the console.',
  },
  network: {
    title: 'Made for one room and its network.',
    items: [
      'No server and no internet. The PCs find each other on the local network, and a cable is better than Wi-Fi.',
      'The picture travels as H.264 video, encoded and decoded on the graphics card where there is one.',
      'Nobody else on the network can watch the broadcast or the screen pictures, and only your console can lock, control or shut down a PC.',
    ],
  },
  setup: {
    title: 'Set up a room in three steps.',
    more: 'The full setup guide',
    steps: [
      {
        name: 'Install the console',
        text: 'Run Promin-Console-Setup.exe on the PC whose screen you will show, then open Promin from the Start menu.',
      },
      {
        name: 'Install the agent on the other PCs',
        text: 'Run Promin-Agent-Setup.exe on every other PC. It asks for one Install click, and from then on it starts with Windows.',
      },
      {
        name: 'Pair them with a room code',
        text: "Type the same six digits in each agent's window and in the console's Set up devices, then choose Pair. The code itself never crosses the network.",
      },
    ],
    codeLabel: 'An example room code: 482913',
  },
  download: {
    title: 'Download Promin',
    consoleNote: 'For the PC whose screen you show',
    agentNote: 'For every other PC',
    requirements: 'Every PC needs Windows 11, and all of them must be on the same local network.',
    sac: "Turn Smart App Control off on every PC first. Promin's setups are not signed yet, so Windows blocks them while it is on.",
    sacLink: 'How to turn it off',
    smartScreen: 'If Windows says it protected your PC, choose More info, then Run anyway.',
    releases: 'All releases and what changed',
  },
  docs: {
    titleSuffix: 'Promin docs',
    navLabel: 'Docs pages',
  },
  footer: {
    name: 'Promin comes from промінь, Ukrainian for a beam of light.',
    licence: 'Free and open source under the MIT licence.',
  },
};

export type Strings = typeof en;

const uk: Strings = {
  lang: 'uk',
  prefix: 'ua/',
  title: 'Promin: екран одного ПК на кожному ПК у кімнаті',
  description:
    'Безкоштовна програма з відкритим кодом для Windows 11. Показує екран одного ПК на всіх інших через локальну мережу, без сервера й інтернету.',
  otherLanguage: { lang: 'en', prefix: '', label: 'English' },
  nav: { features: 'Можливості', docs: 'Документація', download: 'Завантажити' },
  hero: {
    title: 'Показуйте свій екран на кожному ПК у кімнаті.',
    lead: 'Promin — безкоштовна програма для Windows 11. Вона показує екран одного ПК на всіх інших через локальну мережу. Без сервера, облікового запису й інтернету.',
    download: 'Завантажити',
    source: 'Код на GitHub',
    roomLabel: 'Кімната з дванадцяти ПК. Промінь проходить над ними, і на кожному з’являється той самий екран.',
  },
  features: {
    title: 'Уся кімната — з вашого ПК.',
    more: 'Докладніше про кожну можливість',
    screenshotAlt:
      'Консоль Promin: плитка для кожного ПК у кімнаті, а внизу кнопки блокування, надсилання файлів і трансляції.',
    items: [
      {
        id: 'broadcast',
        name: 'Трансляція',
        text: 'Ваш екран заповнює всі інші дисплеї, а їхні клавіатури й миші не працюють, доки ви не зупините трансляцію.',
        details: [
          'У консолі можна вибрати якість картинки та її чіткість.',
          'ПК, що випав із мережі, сам повертається до трансляції, щойно знову з’явиться.',
          'Можна припинити надсилання на один ПК, не зупиняючи решту.',
          'Картинка передається як відео H.264, яке кодує й декодує відеокарта, якщо вона є.',
        ],
      },
      {
        id: 'screens',
        name: 'Усі екрани перед очима',
        text: 'Кожен ПК має плитку з картинкою свого екрана, яка оновлюється приблизно щосекунди.',
        details: [
          'Картинки оновлюються й під час трансляції.',
          'На кожній плитці видно, чи її ПК готовий, отримує трансляцію, відновлює зв’язок, заблокований чи не в мережі.',
          'Позначайте ПК на плитках, щоб вибрати, які заблокувати, на які надіслати файли, які вимкнути чи перезавантажити.',
        ],
      },
      {
        id: 'lock',
        name: 'Блокування',
        text: 'Заблокуйте один ПК, кілька чи всі, з повідомленням до 100 символів. Блокування тримається навіть після перезавантаження, а зняти його можна одним клацанням.',
        details: [
          'Блокування зберігається на самому ПК, тож воно не зникає після перезавантаження, виходу із системи чи закриття консолі. Після входу в систему воно повертається за мить.',
          'Розблокувати ПК може лише ваша консоль.',
          'ПК, на якому працює консоль, ніколи не блокується, а трансляція не блокує його клавіатуру й мишу.',
        ],
      },
      {
        id: 'files',
        name: 'Надсилання файлів',
        text: 'Надсилайте файли й папки на позначені ПК і відкривайте їх там, щойно вони надійдуть.',
        details: [
          'Вони потрапляють до папки Promin на робочому столі того, хто увійшов у систему.',
          'Позначте «Відкрити на кожному ПК одразу після отримання», і вони відкриються у звичній для них програмі. Програми ніколи не запускаються.',
          'На кожній плитці видно, як просувається надсилання, а невдале можна повторити лише для цього ПК.',
        ],
      },
      {
        id: 'control',
        name: 'Керування ПК',
        text: 'Працюйте на іншому ПК на весь екран своєю клавіатурою й мишею. Копіювання та вставлення працює в обидва боки.',
        details: [
          'Двічі клацніть плитку або відкрийте її панель ⋯ і виберіть «Керувати цим ПК».',
          'Картинка заповнює ваш екран, а панель над нею ховається. З цієї панелі картинку можна перенести в окреме вікно.',
          'Копіювати й вставляти можна текст, до 10 000 символів за раз.',
          'На ПК видно вкладку «Цим ПК керують віддалено», а його власні клавіатура й миша заблоковані.',
          'Лише один ПК за раз, не під час трансляції й ніколи — ПК самої консолі. Керування заблокованим ПК розблоковує його.',
          'Екран блокування Windows, запити адміністратора й екран входу поки недоступні.',
        ],
      },
      {
        id: 'power',
        name: 'Вимкнення й перезавантаження',
        text: 'Вимикайте чи перезавантажуйте позначені ПК наприкінці дня після одного підтвердження.',
        details: [
          'Це відбувається одразу: усі програми на них закриваються без збереження.',
          'ПК, на якому працює консоль, ніколи не вимикається й не перезавантажується.',
        ],
      },
    ],
  },
  featuresPage: {
    title: 'Що вміє Promin.',
    lead: 'Дві програми: консоль на вашому ПК і агент на кожному іншому ПК у кімнаті. Усе, що описано на цій сторінці, робиться з консолі.',
  },
  network: {
    title: 'Створено для однієї кімнати та її мережі.',
    items: [
      'Без сервера й інтернету. ПК знаходять одне одного в локальній мережі, і кабель тут кращий за Wi-Fi.',
      'Картинка передається як відео H.264, яке кодує й декодує відеокарта, якщо вона є.',
      'Ніхто інший у мережі не бачить ні трансляції, ні картинок екранів, а блокувати, керувати чи вимикати ПК може лише ваша консоль.',
    ],
  },
  setup: {
    title: 'Налаштуйте кімнату за три кроки.',
    more: 'Повна інструкція з налаштування',
    steps: [
      {
        name: 'Встановіть консоль',
        text: 'Запустіть Promin-Console-Setup.exe на ПК, екран якого показуватимете, і відкрийте Promin з меню «Пуск».',
      },
      {
        name: 'Встановіть агент на інші ПК',
        text: 'Запустіть Promin-Agent-Setup.exe на кожному іншому ПК. Він попросить лише натиснути «Встановити» і відтоді запускатиметься разом з Windows.',
      },
      {
        name: 'Додайте їх кодом кімнати',
        text: 'Введіть ті самі шість цифр у вікні агента на кожному ПК і в розділі «Налаштувати пристрої» на консолі, потім натисніть «Додати». Сам код ніколи не передається мережею.',
      },
    ],
    codeLabel: 'Приклад коду кімнати: 482913',
  },
  download: {
    title: 'Завантажте Promin',
    consoleNote: 'Для ПК, екран якого ви показуєте',
    agentNote: 'Для кожного іншого ПК',
    requirements: 'На кожному ПК потрібна Windows 11, і всі вони мають бути в одній локальній мережі.',
    sac: 'Спершу вимкніть інтелектуальне керування програмами на кожному ПК. Програми Promin поки не мають цифрового підпису, тож Windows блокує їх, доки цей захист увімкнуто.',
    sacLink: 'Як його вимкнути',
    smartScreen: 'Якщо Windows попередить, що захистила ваш ПК, відкрийте докладні відомості й запустіть програму попри попередження.',
    releases: 'Усі випуски та що в них змінилося',
  },
  docs: {
    titleSuffix: 'Документація Promin',
    navLabel: 'Розділи документації',
  },
  footer: {
    name: 'Promin — це «промінь» латиницею.',
    licence: 'Безкоштовна програма з відкритим кодом за ліцензією MIT.',
  },
};

// The build fails when one language gains a text, or a list item, that the other lacks.
const shape = (o: unknown): unknown =>
  typeof o === 'object' && o !== null ? Object.fromEntries(Object.entries(o).map(([k, v]) => [k, shape(v)])) : typeof o;
if (JSON.stringify(shape(en)) !== JSON.stringify(shape(uk))) throw new Error('English and Ukrainian strings differ in shape');

export const strings = { en, uk };
export type Lang = keyof typeof strings;

export const repo = 'https://github.com/andriykohut/promin';

/** Returns the site URL of `path` (no leading slash, trailing slash kept) in the given language. */
export const link = (lang: Lang, path = '') => import.meta.env.BASE_URL.replace(/\/$/, '') + '/' + strings[lang].prefix + path;
