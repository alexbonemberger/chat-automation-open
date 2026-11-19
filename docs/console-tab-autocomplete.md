# Console V6.7 — autocomplete com TAB

A leitura interativa do console utiliza `Console.ReadKey(intercept: true)`, com edição da linha antes de executar qualquer comando. O interpretador de comandos existente continua sendo responsável por validar e executar as entradas **somente ao pressionar Enter**.

## Atalhos

| Tecla | Efeito |
| --- | --- |
| `Tab` | Completa a opção atual; nas próximas vezes, percorre as sugestões em ordem alfabética e retorna à primeira |
| `Shift+Tab` | Percorre as sugestões na direção inversa |
| `←` / `→`, `Home` / `End` | Move o cursor |
| `Backspace` / `Delete` | Edita a linha |
| `↑` / `↓` | Histórico de comandos da sessão |
| `Esc` | Limpa a linha |
| `Enter` | Executa o comando completo |

## Exemplos

Digite o prefixo indicado, então pressione `Tab`:

```text
setT                 -> setText("")              (cursor entre as aspas)
chat00.setT          -> chat00.setText("")       (cursor entre as aspas)
use G                -> use "GPTBOT_0001"
use "G               -> use "GPTBOT_0001"
chat00.              -> opções chat00.clear(), chat00.getText(), ...
open                 -> open(Mode.NewTab) -> open(Mode.NewWindow) -> início
web.addC             -> web.addChat("","")       (cursor nas primeiras aspas)
```

Após `setT + Tab`, o cursor está entre as duas aspas; digitar `Olá` gera `setText("Olá")`. Em seguida, `Enter` preenche o campo do Edge. `Tab` por si só **nunca envia mensagens**.

O catálogo de chats é consultado novamente a cada sequência de completions. Chats registrados com `web.addChat` durante a sessão estarão disponíveis nas próximas sugestões. O Tab não substitui argumentos já iniciados, exceto para nomes no comando `use`.

## Testes

```powershell
& '.\setup.ps1'
& '.\chat-automation.ps1'
```

O `self-test` verifica completions puros (sem UIA), ordem, navegação circular, inserção de aspas, cursor e nomes cadastrados. Para validar `Console.ReadKey` e o redesenho do terminal, é necessário teste interativo real no Windows.

**Limite:** em entradas/saídas redirecionadas (pipes, execução não interativa), o console permanece com `Console.ReadLine()`, sem autocomplete, preservando scripts existentes.

## Dependência de PRs

Branch `feat/console-tab-autocomplete` parte da implementação V6.6 em `feat/chat-max-limit-observer`. Não modifica a `main` até validação e consolidação ordenada dos PRs #1, #2, #3 e #4.
