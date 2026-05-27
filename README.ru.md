[English](README.md) | [Русский](README.ru.md)

# Filexa2SwarmUI Connector

Filexa2SwarmUI Connector подключает SwarmUI к локальной генерации Filexa, чтобы пользователи Telegram могли запускать T2I и I2I задачи на своем компьютере.
Вкладка коннектора показывает состояние polling, активную задачу, elapsed time, latest upload status и optional debug logging.

Бот: https://t.me/FilexaAIBot

Не связан со SwarmUI, не одобрен и не спонсируется проектом SwarmUI.

## Что это

Это коннектор для тех, кто хочет использовать свой установленный SwarmUI как локальный способ генерации в Filexa.
Вы выбираете local connector в боте, вставляете API URL и token во вкладку `Filexa2SwarmUI Connector`, оставляете SwarmUI запущенным, а Filexa передает задачи на ваш компьютер.

SwarmUI не нужно открывать в интернет: коннектор сам делает исходящие HTTPS/HTTP запросы к Filexa.

## Что внутри

- `Filexa2SwarmUIConnector/` - исходный код SwarmUI extension.
- `API_CONTRACT.md` - bot-side API contract для повторного использования этого коннектора с другим bot/server.
- `README.md` - основное руководство по установке и использованию на английском.
- `README.ru.md` - русское руководство по установке и использованию.
- `LICENSE` - лицензия исходного кода.
- `NOTICE.md` - юридические уведомления и отказы от ответственности.
- `SECURITY.md` - политика сообщения об уязвимостях.

Prebuilt binaries в этом репозитории не распространяются.

## Как установить для пользователя SwarmUI

Этот extension рассчитан на работу только с https://t.me/FilexaAIBot.

1. Установите SwarmUI из официального проекта:
   https://github.com/mcmonkeyprojects/SwarmUI
2. Запустите SwarmUI один раз и завершите first-run setup.
3. Откройте рекомендацию SwarmUI по Flux.2 Klein:
   https://github.com/mcmonkeyprojects/SwarmUI/blob/master/docs/Model%20Support.md#flux2-klein
4. Скачайте Flux.2 Klein checkpoint. Лучше использовать встроенный `Utilities` -> `Model Downloader`
   в SwarmUI или положить файл модели в `SwarmUI\Models\diffusion_models`. Для самого простого старта
   начните с Klein 4B distilled: это меньший и более быстрый вариант; SwarmUI рекомендует `Steps=8`
   и `CFG Scale=1` для distilled Klein models. Klein 9B тяжелее; KV-cache variant в основном нужен
   пользователям с гораздо большим объемом VRAM.
5. Перезапустите SwarmUI после появления модели. SwarmUI сам загрузит меньшие text encoder/VAE
   dependencies, когда они понадобятся.
6. Снова перезапустите SwarmUI, откройте вкладку `Generate`, выберите Flux.2 Klein и проверьте одну
   локальную text-to-image генерацию до подключения бота.
7. Скопируйте `Filexa2SwarmUIConnector` в папку SwarmUI:
   `SwarmUI/src/Extensions/Filexa2SwarmUIConnector`.
8. Перезапустите SwarmUI или запустите SwarmUI update/build script, чтобы extension скомпилировался.
9. Откройте новую вкладку `Filexa2SwarmUI Connector`.
10. Вставьте Filexa API URL и token, которые показал Telegram bot. Сохраните token отдельно, если
    он может понадобиться позже: после сохранения вкладка скрывает token.
11. В настройках local connector в боте задайте SwarmUI model code, steps и cfg, если defaults вам не подходят.
12. Включите коннектор и держите SwarmUI запущенным.

Коннектор использует обычный local API SwarmUI (`GetNewSession`, `GenerateText2Image`) и отправляет
весь трафик исходящими запросами с компьютера пользователя в Filexa. Публичный SwarmUI port не нужен.
Bot-side HTTP contract для совместимых servers описан в `API_CONTRACT.md`.

SwarmUI компилирует extensions внутри своего source tree. Этот репозиторий содержит только исходный
код коннектора. SwarmUI компилирует extension внутри локальной установки SwarmUI пользователя во время
restart/update. Prebuilt binaries в этом репозитории не распространяются.
Extension использует `SixLabors.ImageSharp` для optional JPEG conversion и полагается на версию
ImageSharp, которую уже восстанавливает SwarmUI extension props. Не добавляйте второй ImageSharp
package reference внутри этого extension: duplicate references могут сломать SwarmUI restore.

## Как это работает

- Коннектор делает только исходящие HTTPS/HTTP запросы к Filexa.
- Он не требует открывать SwarmUI port пользователя в интернет.
- Он не удаляет локальные SwarmUI outputs.
- Он делает lazy polling каждые 10 секунд, отправляет обновления статуса task, пока включен, и запускает
  generation только когда Filexa возвращает task.
- I2I reference downloads используют короткие HTTP/1.1 close-connection attempts с retries, затем
  маленькие JSON/base64 chunks, если слабая сеть не может доставить reference одним body. Успешный
  chunk fallback ненадолго кэшируется, чтобы следующие references пропускали заведомо неудачные direct GET.
- Direct result upload ограничен 40 MiB. Если generated file больше, коннектор оставляет его на этом
  компьютере и сообщает completion в Filexa без отправки image bytes.
- Если direct upload fails, fallback использует JPEG payload с 80% quality и переиспользует уже
  converted direct payload, когда compression была включена. Compressed results до 3 MiB используют
  fallback uploads: 50 KB binary chunks, затем 8 KB paced JSON/base64 chunks и наконец safe 4 KB
  JSON/base64 mode без долгих retry loops для каждого mode. Если compressed result все еще больше
  3 MiB, коннектор оставляет его на этом компьютере и сообщает completion, вместо того чтобы долго
  пытаться выполнить заведомо неудачный upload. Самый медленный safe JSON/base64 upload использует
  `Connection: close` и pauses between chunks. Успешный JSON/base64 mode кэшируется локально на
  несколько часов; пока cache активен, коннектор пропускает direct upload и сразу использует cached text mode.
- Кнопка `Cancel active task` просит Filexa отменить текущую task и возвращает коннектор к polling
  новых tasks.

## Если что-то не работает

### Хочу обновить или сбросить extension, но SwarmUI показывает старые данные.

Если после update SwarmUI все еще показывает старые данные extension, удалите
`SwarmUI\src\bin\extensions\SwarmExtensionFilexa2SwarmUIConnector` и перезапустите SwarmUI.

### Где менять model code, steps и cfg?

Откройте Filexa и перейдите в Local generation -> Local connector settings.

### Результат с моего компьютера не загружается обратно в Filexa.

Если отправка result зависает, а в SwarmUI terminal видно много неудачных строк `Upload attempt`,
скорее всего причина в network configuration: network, MTU или route. Попробуйте virtual private
network или другой network path.

### Все зависло, подсказки не помогают, extension не реагирует, а Filexa ждет без ошибок.

Обычно помогает restart SwarmUI; сначала отмените task в Filexa через `/cancel`. Если это не
помогло, удалите extension и установите latest version из Git заново.

**‼️ Важно: разработчик и bot не имеют доступа к компьютеру пользователя. Все операции с third-party
software, downloaded models и local configuration выполняются пользователем на свой риск. Разработчик
не несет ответственности за качество результатов, сбои software, hardware damage, data loss или любые
другие потери, вызванные этими действиями. Используйте generative models строго согласно их license!**

## Юридическое уведомление

Этот репозиторий содержит только исходный код Filexa2SwarmUI Connector.

Коннектор распространяется по MIT License. Filexa bot/API service предоставляется на основании
отдельных Filexa Terms of Use и Privacy Policy:
https://teutonick.github.io/bot-legal-docs/privacy

Этот коннектор не является частью SwarmUI, не связан с проектом SwarmUI и не одобрен им. SwarmUI,
AI models, model weights, checkpoints, drivers и другие runtime components являются third-party
software и могут иметь собственные licenses и restrictions.

Пользователи самостоятельно отвечают за установку SwarmUI, выбор и лицензирование models, защиту
API tokens, эксплуатацию своего компьютера, проверку generated outputs, а также соблюдение
применимых законов и third-party terms.

Коннектор выполняет исходящие HTTP/HTTPS requests к настроенному Filexa API endpoint. Он не требует
открывать local SwarmUI port пользователя в public internet.

Коннектор хранит Filexa API URL и token в локальном SwarmUI extension configuration file. Любой, кто
имеет доступ к этому local file, может получить token. Защищайте свою установку SwarmUI и user account.

## Уведомление о безопасности

О проблемах безопасности следует сообщать в приватном порядке согласно `SECURITY.md`.
