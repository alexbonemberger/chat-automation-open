# Detecção de limite máximo da conversa (UIA3)

## Caso de referência

Na conversa GPTBOT_0001, URL de teste:

`https://chatgpt.com/g/g-p-caaca3769873860ca7b78979d56ef499-rm-0-ma-gptbot-base-capabilities/c/1d7a0124-91da-6bbe-d5e3-fec6ec8936cc`

A interface apresenta o aviso **"Você chegou à duração máxima desta conversa, mas pode continuar conversando em um novo chat."** e o botão **"Iniciar novo chat"**. A detecção requer **ambos** os sinais visíveis na árvore de acessibilidade da conversa (não basta uma referência a "limite" dentro do conteúdo do usuário).

Após a auditoria de 18/11/2026, além dos elementos descendentes de `Document:ChatGPT`, existe fallback para o layout UIA achatado do Edge: aceitar controles externos ao documento **somente** com URL de conversa persistente e verificada por guia, um documento visível denominado ChatGPT, aviso específico e exatamente um botão habilitado `Iniciar novo chat`. Sem todas as provas, não gravar o estado de limite. Este fallback possui selftests de regras, mas ainda depende de E2E específico com a tela de limite no Windows.

## Contrato de estado

- `Chat.IsAtMaxLimit` / `chat.isAtMaxLimit`: booleano do catálogo, inicialmente `false`.
- `chat.CheckMaxLimit()` ou `chat.checkMaxLimit()`: inspeciona via UIA **sem alterar foco**, retorna `true` quando já conhecido ou comprovado.
- `chat.InspectMaxLimit()`: devolve relatório com `Detected`, `WarningFound`, `NewChatButtonFound`, `InspectedElements`, `WindowHandle` e `TabRuntimeId`.
- `true` é irreversível para **a mesma URL**. Um novo chat com outra URL começa em `false`. O valor é persistido em `%LOCALAPPDATA%\ChatAutomation\chats.json`.
- Um erro de UIA **não** é representado como `false`. O estado anterior não é modificado.
- Uma conversa marcada como no limite recusa `sendMessage()` com `CHAT_AT_MAX_LIMIT`. O observador nunca clica em `Iniciar novo chat`.
- `busyWindow()` consulta `isBusy()` a cada 8 s. Quando vê `false`, também consulta o limite na primeira leitura ociosa e, se não houver limite, no máximo uma vez por minuto.
- O CMD usa outro processo. Depois da detecção nesse processo, o console interativo pode executar `web.refresh()` para recarregar o estado persistido.
- Escritas concorrentes no catálogo usam um mutex por caminho do arquivo para preservar outros chats e a marcação terminal.

## Teste no Windows (sem envio)

Com a guia GPTBOT_0001 aberta e ativa no Microsoft Edge, no console interativo:

```text
web.addChat("GPTBOT_0001","https://chatgpt.com/g/g-p-caaca3769873860ca7b78979d56ef499-rm-0-ma-gptbot-base-capabilities/c/1d7a0124-91da-6bbe-d5e3-fec6ec8936cc")
use "GPTBOT_0001"
isAtMaxLimit
inspectMaxLimit()
checkMaxLimit()
isAtMaxLimit
```

O primeiro `isAtMaxLimit` será `false` se o chat nunca foi observado; `inspectMaxLimit()` deverá retornar `Detected=true` e o segundo `isAtMaxLimit` deverá ser `true`. O estado permanece `true` após `exit` e reinício. 

Também disponível fora do console:

```powershell
& '.\chat-features.ps1' inspect-max-limit -Chat GPTBOT_0001
& '.\chat-features.ps1' check-max-limit -Chat GPTBOT_0001
```

### Controle negativo

Cadastre ou selecione uma conversa ainda utilizável e execute `inspectMaxLimit()`. Deve resultar `Detected=false`, com ambas as evidências ausentes, e o cache continuar `false`.

### Erros esperados

- `BUSY_TAB_NOT_ACTIVE`: a guia previamente vinculada está em segundo plano; selecionar a guia correta e `setFocus()` explicitamente.
- `AMBIGUOUS_CHAT_TABS`: mais de uma cópia indistinguível ativa; é necessário fixar uma guia exata.
- `MAX_LIMIT_TARGET_CHANGED`: guia/URL mudou durante leitura; resultado descartado.
- `MAX_LIMIT_UIA_NOT_READY`: árvore acessível indisponível; não presumir `false`.
- `CHAT_AT_MAX_LIMIT`: tentativa de envio bloqueada após identificação terminal.

Se `inspectMaxLimit()` retornar `WarningFound=false` ou `NewChatButtonFound=false` no chat comprovadamente no limite, enviar o JSON de diagnóstico; o navegador pode expor o conteúdo com padrões ou nomes UIA diferentes.

## Política de polling

- `busyWindow()`: **8 s** entre atualizações visuais, custo UIA aceito para geração de 30 s a 15 min.
- `PollingLab`: estabilidade mínima de **300 ms** para `false` após observar `true`. A duração real pode ser maior devido à latência de cada consulta UIA.
- `false` em `isBusy()` significa apenas ausência do controle de interrupção na leitura; **não** confirma que a conversa pode aceitar mensagens.
- Sem reenvio após `INPUT_DISPATCHED_UNCONFIRMED`; preservar guia fixada (HWND+RuntimeId).
