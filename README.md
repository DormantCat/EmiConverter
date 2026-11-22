# EMI Converter

Настольная утилита для локального просмотра писем EML, EMI, TXT и вставленного исходника до 32 МиБ.

Утилита показывает:

- отправителя, получателей, копии, адрес для ответа, тему и дату;
- безопасный предпросмотр HTML (в Windows): без скриптов, внешних ресурсов и активных ссылок;
- текст письма и исходник;
- все ссылки из письма.

Сетевые проверки оператор запускает отдельно: WHOIS/RDAP домена и IP, DNS, перенаправления ссылки, трассировка, доступность TCP-сервисов.

- Windows: WinForms, .NET 9, один файл EmiConverter.exe.
- Linux: интерфейс на Avalonia 11.1.3, запуск не проверялся.

## Структура

| Путь | Назначение |
| --- | --- |
| `MailReader.cs` | Разбор MIME, извлечение ссылок, очистка HTML для предпросмотра |
| `DomainLookup.cs`, `DossierLookup.cs`, `RedirectInspector.cs` | Сетевые проверки |
| `MainForm.cs`, `GravityScrollArea.cs`, `HtmlMailPreview.cs`, `Program.cs` | Интерфейс Windows |
| `LinuxTrace.cs`, `Linux/` | Интерфейс и трассировка для Linux |
| `build.proj` | Публикация для всех платформ |

Сборка описана в [README-BUILD.md](README-BUILD.md), сведения для службы ИБ — в [SECURITY-REVIEW.md](SECURITY-REVIEW.md).
