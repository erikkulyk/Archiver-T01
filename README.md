# Розумний захищений архіватор файлів (Проєкт 2, команда T01)

C# / .NET 8. Конвеєр: ознаки → класифікатор (MLP) → блоки по 16 КБ → LZ77 (greedy / lazy / ДП) →
канонічний Гаффман (LIT/LEN/DIST) → NO_COMPRESSION → рюкзак 0/1 для томів → AES-256-CTR + HMAC-SHA256.

## Запуск з нуля
```
dotnet build Archiver.sln -c Release
dotnet test  Archiver.sln -c Release
dotnet run --project src/SmartArchiver -- train teacher_data/ model.json
dotnet run --project src/SmartArchiver -- pack out/arc corpus/ --password algo2026 --model model.json
dotnet run --project src/SmartArchiver -- unpack restored out/arc_000.arc out/arc_001.arc
dotnet run --project src/SmartArchiver -- bench corpus/ --model model.json
dotnet run --project src/SmartArchiver -- ga corpus/ --evals 200 --model model.json
```
Перед запуском розпакуй архів викладача в `teacher_data/` так, щоб усередині були каталоги `train/` і `valid/`. `team_config.json` лежить у корені (seed, пароль, α/β/t_ref, ліміт тому, пріоритети). Для `pack`, `bench` і `ga` передавай `--model model.json`, інакше програма використовує резервний клас 0 для всіх файлів. Команда `train` читає `file,type,type_name,size` з `labels.csv`; клас береться саме з колонки `type`. Якщо valid macro-F1 нижче `F1_min`, модель зберігається для аналізу, але команда завершується з кодом 5.

## Зони → файли
| Зона | Файли |
|---|---|
| Стиснення і розбір потоку | `Huffman.cs`, `Lz77.cs`, `BlockCodec.cs` |
| Аналіз файлу і оптимізація | `Classifier.cs`, `Genetic.cs`, `Metric.cs` |
| Захист і пакування в томи | `Crypto.cs`, `Crc32.cs`, `Knapsack.cs`, пакування в `ArchiveService.cs` |
| Інтеграція | `Format.cs`, `ArchiveService.cs`, `Config.cs`, `Program.cs`, `tests/`, CI |

## Сумісність із еталонами
Поля заголовка, payload, reserved-поле, представлення порожнього файла, nonce/CTR та розріджені таблиці Гаффмана звірено з еталонними архівами викладача. У тестах є перевірка декодування еталонного архіву й еталонного стисненого блоку. Повний прогін усіх `reference/ref_*.arc` та `expected_failures.json` потрібно виконати локально командою `dotnet test` / додатковим інтеграційним прогоном.
