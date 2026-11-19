# Contexto único com `use` — revisão de `bindToChat`

## Achado

No V6.7 existiam dois estados de seleção que podiam divergir:

1. `WebInterface._selectedChatName`, selecionado no console por `use "chat00"`.
2. `CurrentBinding.currentBindedChat`, persistido em `%LOCALAPPDATA%\ChatAutomation\current-binded-chat.json` e atualizado por `bindToChat`, `setFocus()` e `open()`.

O console moderno, com `use`, resolve todas as operações nomeadas via `RequireCurrentChat()`. Entretanto, a CLI legada possui chamadas que usam `CurrentBinding.Require()`, independentemente de `use`. Por isso, o estado legado podia apontar para outra conversa mesmo enquanto o console mostrava `chat-automation[chat00]`.

## Contrato recomendado (V6.8)

- **`use "nome"`**: é a única operação que altera o chat selecionado na sessão do console. Não muda foco no navegador e não escreve `CurrentBinding`.
- **`web.addChat("nome","url")`**: cadastra URL explicitamente, sem selecionar ou mudar a guia.
- **`web.captureChat("nome")`**: cadastra ou atualiza o pin do chat com base na guia ativa do Edge, **sem selecionar contexto** nem escrever o vínculo global legado. Se o nome já corresponde a outra URL, bloqueia com `CAPTURE_URL_CONFLICT`.
- **`setFocus()` / `open()`**: atualizam o vínculo da guia apenas no registro `ChatRecord`, não em `CurrentBinding`.
- **`bindToChat()`**: removido da interface interativa moderna. Invocá-lo informa a substituição: `web.captureChat("nome")` e `use "nome"`.
- **APIs e scripts legados**: `WebInterface.BindToChat` e `bind-to-chat.ps1` permanecem temporariamente disponíveis para não quebrar automações existentes, mas marcados como legados. O estado `CurrentBinding` não é fonte de seleção para o console moderno.
- **Inicialização**: o console não carrega mais `current-binded-chat.json` automaticamente nem importa esse vínculo ao catálogo; a migração antiga continua disponível por opção explícita `migrateLegacy: true` em C#. Rotas CLI legadas que dependem desse arquivo continuam carregando-o.
- **`chat00.setText("texto")`** continua permitido para chamadas explicitamente endereçadas; não altera o `use` atual.

## Fluxos

Cadastro por URL:

```text
web.addChat("chat00","https://chatgpt.com/c/ID")
use "chat00"
setText("rascunho")
```

Cadastro da guia ativa (sem informar URL):

```text
web.captureChat("GPTBOT_0001")
use "GPTBOT_0001"
isAtMaxLimit
```

Troca rápida entre dois chats cadastrados:

```text
use "chat00"
getText()
use "GPTBOT_0001"
isAtMaxLimit
```

O `use` é local à sessão. Outras instâncias independentes do console podem selecionar outros chats; alterações persistentes no catálogo usam o mutex existente.

## Testes

O `self-test` verifica:

- sugestões do `Tab` incluem `web.captureChat("")`, mas não `bindToChat`;
- `captureChat` com URL explícita preserva `CurrentBinding` e a seleção feita via `use`;
- tentativa de capturar um nome existente apontando para outra URL falha com `CAPTURE_URL_CONFLICT`;
- `bindToChat()` interativo retorna orientação de migração, sem alterar o estado;
- `use` após cadastro seleciona o registro correto.

**Teste real no Windows:** usar `web.captureChat("nome")` com a guia correta ativa no Edge, sem mudar `use` até chamar `use "nome"` explicitamente. Nunca testar a captura com uma guia diferente usando um nome já existente sem esperar bloqueio.
