# План исправлений по результатам аудита Keyword Cleaner

## Сводка проблем

| # | Уровень | Проблема | Файл |
|---|---------|----------|------|
| 1 | 🔴 | Провалившийся пул уходит в cleaned неочищенным | [`KeywordCleanerService.cs:234-238`](../KeywordClusterizer/KeywordCleanerService.cs:234) |
| 2 | 🔴 | Retry не различает «стоит повторить» и «бесполезно» | [`DeepSeekHelper.cs:102-108`](../KeywordClusterizer/DeepSeekHelper.cs:102) |
| 3 | 🔴 | Thundering herd при rate limit | [`KeywordCleanerService.cs:223`](../KeywordClusterizer/KeywordCleanerService.cs:223) |
| 4 | 🟠 | Missed keywords recovery тихо переопределяет решение AI | [`KeywordCleanerService.cs:259`](../KeywordClusterizer/KeywordCleanerService.cs:259) |
| 5 | 🟠 | Дубликаты во входном файле искажают статистику | [`KeywordCleanerService.cs:242-244`](../KeywordClusterizer/KeywordCleanerService.cs:242) |
| 6 | 🟠 | API-ключи вводятся в открытом виде | [`Program.cs:213`](../KeywordClusterizer/Program.cs:213), [`401`](../KeywordClusterizer/Program.cs:401), [`509`](../KeywordClusterizer/Program.cs:509) |
| 7 | 🟡 | 30 мин таймаут × 3 ретрая × семафор = до 90 мин блокировки | [`Program.cs:538`](../KeywordClusterizer/Program.cs:538) |
| 8 | 🟡 | 3 из 5 пунктов меню моделей — заглушки | [`Program.cs:483,489,495`](../KeywordClusterizer/Program.cs:483) |

---

## 1. 🔴 Провалившийся пул → отдельный `failed.txt`

**Где:** [`KeywordCleanerService.cs:227-238`](../KeywordClusterizer/KeywordCleanerService.cs:227)

**Что сейчас:** При `response == null` после 3 попыток все ключи пула молча добавляются в `allCleaned`.

**Что нужно:**
- Добавить `ConcurrentBag<string> allFailed` в сигнатуру [`ProcessPoolAsync()`](../KeywordClusterizer/KeywordCleanerService.cs:190) и в вызывающий код [`RunAsync()`](../KeywordClusterizer/KeywordCleanerService.cs:52).
- При провале пула — ключи идут в `allFailed`, **не** в `allCleaned`.
- В [`SaveResults()`](../KeywordClusterizer/KeywordCleanerService.cs:335) добавить сохранение `failed.txt` с подсчётом.
- Вывести предупреждение пользователю о наличии необработанных пулов.

**Дополнительно:** Рассмотреть добавление опции `--retry-failed` для повторной обработки только упавших ключей.

---

## 2. 🔴 Умная retry-стратегия в `DeepSeekHelper`

**Где:** [`DeepSeekHelper.cs:102-108`](../KeywordClusterizer/DeepSeekHelper.cs:102)

**Что сейчас:** Любой не-успешный статус → `return null` без разбора причины. Retry-логика на стороне [`KeywordCleanerService.cs:207-225`](../KeywordClusterizer/KeywordCleanerService.cs:207).

**Что нужно:**
- Изменить [`SendRawRequestAsync()`](../KeywordClusterizer/DeepSeekHelper.cs:33) так, чтобы метод **возвращал информацию о причине ошибки**, а не только `null`.
- Вариант А: вернуть `(T? result, CleanerErrorType? errorType)`.
- Вариант Б: кидать custom exception с типом ошибки.

- **Не ретраить:**
  - `401` — неверный ключ → сразу прервать весь процесс, вывести `[КРИТИЧНО] Неверный API ключ`.
  - `400` — битый промпт → не ретраить, логировать тело запроса для отладки.
- **Ретраить с экспоненциальным backoff + jitter:**
  - `429` — rate limit.
  - `500+` — серверная ошибка.
  - Network timeout / `HttpRequestException`.

- **Перенести retry-логику из [`KeywordCleanerService.cs`](../KeywordClusterizer/KeywordCleanerService.cs) в [`DeepSeekHelper`](../KeywordClusterizer/DeepSeekHelper.cs)** с единым методом `SendWithRetryAsync()`.

---

## 3. 🔴 Jitter / Exponential backoff вместо фиксированной задержки

**Где:** [`KeywordCleanerService.cs:223`](../KeywordClusterizer/KeywordCleanerService.cs:223)

**Что сейчас:** `Task.Delay(5000)` — все потоки ждут одинаково и бьют синхронно.

**Что нужно:**
- Реализовать экспоненциальный backoff: `baseDelay * 2^attempt + random_jitter`.
- Пример: попытка 1 → ~5s, попытка 2 → ~10s, попытка 3 → ~20s (с jitter ±20%).
- Jitter = `Random.Shared.Next(-1000, 1000)` мс к базовой задержке.
- Разнести retry-паузу по времени между потоками.

---

## 4. 🟠 Улучшить «Missed keywords recovery»

**Где:** [`KeywordCleanerService.cs:256-278`](../KeywordClusterizer/KeywordCleanerService.cs:256)

**Что сейчас:** 
- Сравнение через `Intersect` с `OrdinalIgnoreCase` — если AI изменил пунктуацию/пробелы, ключ не находится.
- Missed-ключи при `SeparateFile` молча уходят в `cleaned`, переопределяя решение AI.

**Что нужно:**
- **Добавить нормализацию перед сравнением:**
  - `Trim().ToLowerInvariant()` с обеих сторон.
  - Удаление лишних пробелов (squeeze).
- **Логировать missed-ключи** с указанием причины (не найдены в ответе AI).
- **Решить судьбу missed-ключей осознанно:**
  - `BrandHandling.SeparateFile`: логировать предупреждение + добавить комментарий в `cleaned.txt` (или отдельный `.missed` файл).
  - `BrandHandling.ToDiscarded`: missed → discarded (уже так).
  - `BrandHandling.KeepAsIs`: missed → cleaned (уже так).
- Рассмотреть fuzzy-match (Levenshtein) с порогом, если точное совпадение не найдено.

---

## 5. 🟠 Дедупликация на уровне входных данных

**Где:** [`KeywordCleanerService.cs:242-244`](../KeywordClusterizer/KeywordCleanerService.cs:242)

**Что сейчас:** `Distinct()` применяется только на ответе AI, но `processedCount` считает сырой `poolKeywords.Count`.

**Что нужно:**
- Дедуплицировать **входной список ключей** перед разбивкой на пулы (в [`RunAsync()`](../KeywordClusterizer/KeywordCleanerService.cs:52) или перед вызовом).
- Вывести предупреждение: `[ВНИМАНИЕ] Удалено {n} дубликатов из входного файла`.
- Статистика в конце будет сходиться: `processedCount == cleaned+discarded+branded`.

---

## 6. 🟠 Маскировать ввод API-ключей

**Где:** [`Program.cs:213`](../KeywordClusterizer/Program.cs:213), [`401`](../KeywordClusterizer/Program.cs:401), [`509`](../KeywordClusterizer/Program.cs:509)

**Что сейчас:** `Console.ReadLine()` — ключ виден при вводе.

**Что нужно:** Написать helper-метод `ReadPassword()`:
```csharp
static string ReadPassword()
{
    var password = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true); // intercept = true — не выводить символ
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            password.Length--;
        else if (!char.IsControl(key.KeyChar))
            password.Append(key.KeyChar);
    }
    Console.WriteLine();
    return password.ToString();
}
```
Затем заменить все 3 вызова `Console.ReadLine()` для ключей на `ReadPassword()`.

---

## 7. 🟡 Сократить HttpClient.Timeout и добавить CancellationToken

**Где:** [`Program.cs:538`](../KeywordClusterizer/Program.cs:538)

**Что сейчас:** `HttpClient.Timeout = TimeSpan.FromMinutes(30)`.

**Что нужно:**
- Уменьшить до `TimeSpan.FromMinutes(3)` — 3 минут на один запрос AI более чем достаточно.
- Если нужен большой `max_tokens` — выставить `HttpClient.Timeout` динамически: `TimeSpan.FromMinutes(max_tokens / 100 + 1)`.
- Добавить `CancellationToken` в цепочку вызовов, чтобы можно было прервать зависший запрос без ожидания таймаута.

---

## 8. 🟡 Убрать заглушки моделей или заменить на реальные ID

**Где:** [`Program.cs:483,489,495`](../KeywordClusterizer/Program.cs:483)

**Что сейчас:** Три пункта меню с `TODO: уточнить model ID на OpenRouter`.

**Что нужно — два варианта:**

### Вариант А (рекомендуемый): Убрать до готовности
- Уменьшить меню до 2 пунктов (DeepSeek Pro / DeepSeek Flash).
- Остальные модели добавить, когда появятся реальные model ID и будет протестировано.

### Вариант Б: Добавить реальные model ID (требует исследования)
- Qwen: `qwen/qwq-3.6` (или актуальный ID на OpenRouter).
- Claude: `anthropic/claude-sonnet-5` (или `anthropic/claude-4` — уточнить).
- Gemini: `google/gemini-2.5-pro` (Gemini 3.1 не существует).
- **Важно:** проверить, поддерживают ли эти модели `response_format: { type: "json_object" }`.

---

## 9. (NEW) Добавить отдельный пул для failed-ключей в `CleanerSettings`

**Где:** [`CleanerSettings.cs`](../KeywordClusterizer/Models/CleanerSettings.cs)

**Что нужно:** Добавить свойство:
```csharp
public string OutputFailed { get; set; } = "failed.txt";
```

---

## Mermaid: новый поток обработки ошибок

```mermaid
flowchart TD
    A[Запрос к API] --> B{Успех?}
    B -->|Да| C[Парсим CleanerResponse]
    B -->|Нет| D{Код ошибки?}
    
    D -->|401| E[НЕ РЕТРАИТЬ<br/>Прервать процесс<br/>Неверный ключ]
    D -->|400| F[НЕ РЕТРАИТЬ<br/>Логировать запрос<br/>Пропустить пул]
    D -->|429 / 5xx / timeout| G[Ретраить]
    
    G --> H{Попыток < 3?}
    H -->|Да| I[Задержка: 2^attempt * 5s + jitter]
    I --> A
    
    H -->|Нет| J[Пул ПРОВАЛИЛСЯ]
    J --> K[allFailed.AddRange(poolKeywords)]
    
    C --> L{AI вернул все ключи?}
    L -->|Нет| M[missed = ключи не в ответе]
    M --> N{BrandHandling}
    N -->|SeparateFile| O[Лог + missed в cleaned?<br/>Или отдельный файл]
    N -->|ToDiscarded| P[missed в discarded]
    N -->|KeepAsIs| Q[missed в cleaned]
    
    subgraph "Финальное сохранение"
        R[cleaned.txt]
        S[discarded.txt]
        T[bсanded.txt]
        U[<b>failed.txt</b>]
    end
    
    K --> U
```

---

## План работ (Todo)

### Блок A: Критические баги
- [ ] **A1.** Перенаправить провалившиеся пулы в `failed.txt` вместо `cleaned`
  - Изменить [`KeywordCleanerService.cs`](../KeywordClusterizer/KeywordCleanerService.cs): добавить `allFailed`, изменить `SaveResults()`, добавить `failed.txt`
  - Изменить [`CleanerSettings.cs`](../KeywordClusterizer/Models/CleanerSettings.cs): добавить `OutputFailed`

- [ ] **A2.** Внедрить умную retry-стратегию
  - Изменить [`DeepSeekHelper.cs`](../KeywordClusterizer/DeepSeekHelper.cs): возвращать тип ошибки, не ретраить 401/400
  - Перенести retry-логику в `SendWithRetryAsync()`
  - Добавить экспоненциальный backoff + jitter (в [`KeywordCleanerService.cs`](../KeywordClusterizer/KeywordCleanerService.cs) или в helper)

### Блок B: Проблемы целостности данных
- [ ] **B1.** Нормализация missed-ключей + логирование
  - [`KeywordCleanerService.cs:256-278`](../KeywordClusterizer/KeywordCleanerService.cs:256): нормализовать сравнение, логировать missed, осознанная маршрутизация

- [ ] **B2.** Дедуплицировать входные данные до обработки
  - [`KeywordCleanerService.cs`](../KeywordClusterizer/KeywordCleanerService.cs): дедуплицировать перед `SplitIntoPools()`, вывести предупреждение

### Блок C: Безопасность и UX
- [ ] **C1.** Реализовать `ReadPassword()` и заменить `Console.ReadLine()` для ключей
  - [`Program.cs:213,401,509`](../KeywordClusterizer/Program.cs:213)

- [ ] **C2.** Уменьшить `HttpClient.Timeout` + добавить CancellationToken
  - [`Program.cs:538`](../KeywordClusterizer/Program.cs:538) и цепочка вызовов до `DeepSeekHelper`

### Блок D: Чистота кода
- [ ] **D1.** Убрать или заменить модели-заглушки в меню
  - [`Program.cs:483,489,495`](../KeywordClusterizer/Program.cs:483): убрать пункты 3-5 до появления реальных model ID

### Блок E: Тестирование
- [ ] **E1.** Проверить сценарий неверного API-ключа — процесс должен прерваться сразу, без 57 бесполезных запросов
- [ ] **E2.** Проверить сценарий rate limit — jitter должен разнести ретраи по времени
- [ ] **E3.** Проверить сценарий сбоя сети — пул уходит в `failed.txt`, а не в `cleaned`
- [ ] **E4.** Проверить статистику после дедупликации — сумма сходится
