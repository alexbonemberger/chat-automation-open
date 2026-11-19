## 18/11/2026 — Eliminar duplicação visual de PRO_xxxx no prompt

Validação Windows do commit `cb1a79d`: o chat
`PRO_0001-GPTBOT_0003` foi reconhecido pela UIA com
`AUTO_DETECTED`, `matchingPageElements=4`, título
`Continuidade de sistemas`, e `captureFullName()`
persistiu `UIA_TAB_AND_PAGE`. O prompt mostrava o ID
com `PRO_0001` dentro dos colchetes e novamente à direita.

**Ajuste apenas visual:**

```text
autonomous-cycle-test[GPTBOT_0003 - Continuidade de sistemas|PRO_0001]>
autonomous-cycle-test[GPTBOT_0004|PRO_0001]>
```

`ConsoleShell.FormatPrompt` remove do texto exibido
somente o prefixo `<projectId>-` quando ele corresponde
exatamente ao `projectId` do final. Sem título `fullName`,
não exibe o separador de título. Em projeto diferente ou
ausente, mantém o identificador completo.

**Invariantes:** o nome persistido em `chats.json`, a
seleção `use "PRO_0001-GPTBOT_0003"`, o autocomplete,
o vínculo do projeto e a URL não são modificados. Testes
verificam cenário com e sem fullName, projeto diferente,
nenhum projeto e chat sem prefixo. Mudança ainda aguarda
compilação e self-test no Windows.


## 18/11/2026 — Título de aba Edge com métrica de memória + prompt com fullName

**Evidência Windows confirmada:** no commit `1fa66b43`, o usuário
executou `use "PRO_0001-GPTBOT_0003"`, `setFocus()`
e `inspectFullName()`. A URL e o pin foram comprovados
(`HWND=1180792`, `RuntimeId=42.1180792.4.0.0.257501`),
mas o rótulo do `TabItem` chegou como
`Continuidade de sistemas - Uso de memória - 597 MB`,
com `matchingPageElements=0` e
`UIA_TITLE_UNCONFIRMED`. O usuário salvou
`setFullName("Continuidade de sistemas")`, recebendo
`FULL_NAME_SAVED` com `source=MANUAL`; porém o prompt
continuava exibindo apenas `PRO_0001-GPTBOT_0003`.

**Correções publicadas:**

1. `NormalizeTabTitle` remove sufixo **ancorado no fim**
   ` - Uso de memória - <número> MB/GB/KB`
   (e variantes inglesas) sem alterar o nome-base.
2. `ChatFullNameInspector.Inspect` continua exigindo a
   mesma URL e RuntimeId antes/depois da leitura do TabItem.
   Se houver concordância com texto da página, retorna
   `AUTO_DETECTED` (`UIA_TAB_AND_PAGE`).
   Se a página não expuser título mas o TabItem tiver nome
   válido, retorna `AUTO_DETECTED_TAB_ONLY`, cujo metadado
   `fullNameSource` será `UIA_TAB_VERIFIED`.
   Não depender do primeiro registro em Recentes.
3. `captureFullName()` e captura automática de chat novo
   aceitam ambas as evidências e salvam sua origem explícita.
   `setFullName()` continua manual e preserva o ID.
4. `ConsoleShell.FormatPrompt` recebe `chatFullName`
   opcional e mostra `<ID> - <fullName>` apenas quando ambos
   estão cadastrados, sem `null` ou pontuação extra caso
   `fullName` seja vazio.
5. `ConsoleShell` usa `object? result` para evitar
   o aviso de anulabilidade `CS8600` ao consultar
   `getFullName()` ainda não cadastrado.

**Exemplos:**

```text
autonomous-cycle-test[PRO_0001-GPTBOT_0003 - Continuidade de sistemas|PRO_0001]>
autonomous-cycle-test[PRO_0001-GPTBOT_0004|PRO_0001]>
```

**Teste não destrutivo:** compilar e iniciar a revisão atualizada,
selecionar `PRO_0001-GPTBOT_0003`, confirmar que o nome
manual já salvo aparece no prompt; então executar
`inspectFullName()` e `captureFullName()` na guia fixada.
Se retornar `FULL_NAME_SAVED` com
`source=UIA_TAB_VERIFIED`, a leitura automática da aba
foi comprovada. Verificar `getFullName()` e
`web.projectListChats("PRO_0001")`. Nenhuma nova
conversa nem mensagem é necessária.

**Validação:** testes puros foram adicionados para sufixos
de memória, estados de origem e prompt com/sem fullName.
Compilação e captura E2E do novo commit ainda pendentes.


## 18/11/2026 — Nome completo (fullName) do chat a partir da UIA

A identidade persistente `Name` (ex. `PRO_0001-GPTBOT_0003`)
não representa o título visível no ChatGPT (ex.
`Controlar UIA com C# 2`, `Continuidade de sistemas`).
Foi adicionado `fullName` e `fullNameSource` ao registro de cada
chat em `chats.json`. `displayTitle` permanece o título opcional
informado na operação de criação e **não é presumido** como título
realmente exibido. Arquivos anteriores permanecem compatíveis:
campos novos ausentes são tratados como `null`.

**Captura automática na criação:** após o gate `isBusy=false`,
detectar URL definitiva e executar `CompleteNewChat`, tenta até
3 observações UIA passivas. Usa o `TabItem` da guia vinculada
por HWND/RuntimeId/URL e verifica que outro elemento **visível da
página** (texto, botão, hyperlink, list item ou grupo fora da barra
de abas do Edge) contém exatamente o mesmo título. Exclui filhos
de TabItem, títulos genéricos ChatGPT/Novo chat e sufixos conhecidos
` - ChatGPT`. Não assume que o primeiro item em Recentes é o
chat selecionado. Se não confirmar, mantém `fullName=null` sem
bloquear o registro de URL nem repetir a mensagem.

**Cadastro a partir da guia ativa:** `web.captureChat("nome")`
já lê a URL da guia; agora também tenta capturar o `fullName`
da mesma guia com evidência UIA. No caso de URL fornecida
explicitamente (`web.captureChat("nome","url")` pela API), não
ativa o Edge para descobrir título. Se a UIA for inconclusiva,
o chat permanece registrado com `fullName=null`.

**Comandos para chat já registrado no console:**

```text
use "PRO_0001-GPTBOT_0003"
setFocus()
inspectFullName()
captureFullName()
getFullName()
listChats()
web.projectListChats("PRO_0001")
```

`inspectFullName()` mostra uma prévia com `browserTabTitle`,
`matchingPageElements` e status `AUTO_DETECTED` ou
`UIA_TITLE_UNCONFIRMED` sem gravar. `captureFullName()`
grava somente título confirmado, com
`fullNameSource=UIA_TAB_AND_PAGE`. Se a UIA não expuser
evidência, use:

```text
setFullName("Título mostrado no ChatGPT")
PRO_0001-GPTBOT_0003.setFullName("Título mostrado no ChatGPT")
```

`setFullName` persiste `fullNameSource=MANUAL`
sob lock e valida a URL registrada. Não muda a URL, não renomeia
a conversa na interface do ChatGPT, não altera o nome lógico
`GPTBOT_XXXX` nem envia mensagem.

`getFullName()`, `listChats()`, `web.projectListChats(...)`
e `use` exibem o metadado. A captura automática só preenche
`fullName` vazio, preservando registros manuais anteriores.

**Segurança e limitações:** a captura requer guia correta selecionada
na janela do Edge; `setFocus()` é necessário quando ela ficou
em segundo plano. A UIA pode não expor um título igual ao do
TabItem na página; nesse caso o status permanece inconclusivo.
Título visual não substitui URL e RuntimeId como identidade.

**Validação pendente:** compilação/self-test em Windows e
captura UIA real de um chat existente; os testes puros validam
normalização, persistência do catálogo, CLI e autocomplete.
Não criar nova conversa apenas para testar o nome.


## 18/11/2026 — Vincular novo chat somente após geração concluir

**Evidência real anterior:** o `createProjectChat` de
`PRO_0001-GPTBOT_0002` retornou `REGISTERED` com URL persistente
`https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260-autonomous-cycle-test/c/18e45dd6-bab8-69c6-9618-fc90b7a4fe3e`
e pin `HWND=1180792`,
`RuntimeId=42.1180792.4.0.0.257501`. A execução comprovou
o primeiro envio e o vínculo, mas **não aguardou isBusy=false**.
O usuário solicitou que o cadastro seja a última operação, depois
da geração.

**Fluxo atualizado de `web.createProjectChat`:**

1. O envio inicial continua sendo feito **uma única vez**, com
   `FIRST_SEND_ATTEMPTED` persistido antes do clique.
2. Observar a **mesma** guia (HWND/RuntimeId) até existir URL
   `/c/<id>` persistente no projeto correto.
3. Na mesma URL/guia, consultar `ChatFeatures.IsGenerating`
   (equivalente ao `isBusy`) a cada 250 ms, com limite de 60 s.
   O detector agora reconhece um único botão `Stop` fora do
   `Document:ChatGPT` quando a árvore UIA é achatada.
4. Só interpretar fim da geração quando houver **duas leituras
   ociosas consecutivas após busy=true**; se o início da geração
   não tiver sido capturado, exigir **oito leituras ociosas e
   pelo menos 2 s** com a URL persistente.
5. Reconfirmar a URL definitiva **novamente** e o RuntimeId
   com `ActivateTabAndReadCurrentUrl`; só então executar
   `CompleteNewChat`, gravando URL/pin no catálogo,
   removendo a reserva e selecionando o novo chat/projeto.

**Estados seguros:**
- `REGISTERED`: URL/pin confirmados depois do estado ocioso;
  a nota contém `busyObserved`, `idleReads` e `uiReads`.
- `PROJECT_RESPONSE_WAIT_PENDING`: timeout; reserva preservada.
- `PROJECT_RESPONSE_FOCUS_CHANGED`,
  `PROJECT_RESPONSE_URL_CHANGED`,
  `PROJECT_RESPONSE_TARGET_CHANGED` ou
  `PROJECT_RESPONSE_FINAL_URL_CHANGED`: alteração de identidade;
  reserva preservada e **nenhum novo envio**.

`web.reconcileProjectChat("PRO_xxxx-GPTBOT_xxxx")` recupera
`FIRST_SEND_ATTEMPTED` e retoma **somente** observações UIA,
sem digitação, Enter nem clique. No timeout ou em incerteza,
não executar uma segunda criação do mesmo chat.

**Validação requerida no Windows:** compilar, executar self-tests
e criar UMA nova conversa para testar o gate. O chat
`PRO_0001-GPTBOT_0002` já está `REGISTERED`; não o recriar.
O próximo número previsto do projeto será
`PRO_0001-GPTBOT_0003`, sujeito ao catálogo atual.
Verificar que não retorna `REGISTERED` durante geração ativa,
que a URL observada pertence à mesma guia e que o registro/pending
são coerentes após a resposta.


## 18/11/2026 — Recuperação de /project após reiniciar console sem USE

**Falha E2E observada:** `None[-|None]`, `pendingNewChats()=[]`,
`web.createProjectChat("PRO_0001", HANDOFF_V7_1, ...)` respondeu:
```text
PROJECT_COMPOSE_SOURCE_NOT_FOUND: não há /project ou conversa
registrada do mesmo projeto selecionada.
```

A tentativa foi bloqueada **antes de `Reserve()` e antes do envio**.
O erro não comprova que a página `/project` deixou de existir:
a versão anterior consultava só a guia selecionada de cada janela.
Se a guia `/project` estava em segundo plano e o console iniciou com
`USE=None`, ela ficava invisível para essa lógica.

**Correção:** ao não encontrar `/project` selecionado, varrer as
URLs exatas das guias do Edge usando `EdgeBrowser.ScanExact()`,
que restaura a seleção anterior. Aceitar recuperação apenas se o scan
não informar avisos, encontrar exatamente uma guia cuja URL seja
`https://chatgpt.com/g/g-p-<projeto>/project` e possuir `RuntimeId`.
Nesse caso, ativar a identidade via `ActivateExactTab()`,
validar URL e RuntimeId novamente, e prosseguir com a checagem do
editor antes da reserva do novo ID. Caso contrário, falhar sem enviar.

A recuperação não cria guia, não gera mensagem, não reaproveita
uma URL qualquer e não escolhe a primeira ocorrência; múltiplas
cópias ou enumeração UIA incompleta continuam bloqueadas.

**Etapa de validação:** atualizar a branch e comprovar
`PASS_COMPILE_AND_SELFTEST`, depois `pendingNewChats()`. Caso
`pending=[]`, repetir **uma única vez** o comando de criação com
o mesmo handoff. Se aparecer uma reserva `FIRST_SEND_ATTEMPTED`,
não repetir o envio: utilizar `web.reconcileProjectChat`.


## 18/11/2026 — Terceira conversa bloqueada pelo layout UIA achatado do Edge

A tentativa de `web.createProjectChat("PRO_0001", HANDOFF_V7_1, ...)`
foi bloqueada por `PROJECT_COMPOSER_NOT_UNIQUE` com:
```text
candidatos=0
VisibleEditors=[Edit:Barra de pesquisa e endereços#view_1017 insideDocument=False keyboardFocusable=True |
                Document:ChatGPT#RootWebArea insideDocument=False keyboardFocusable=True |
                Edit:# insideDocument=False keyboardFocusable=False]
Nenhum envio realizado.
```

**Raiz:** `FindComposer` exigia que o único editor anônimo fosse
descendente de `Document:ChatGPT`. O Edge 157 o apresentou como um
`Edit` separado do documento. `IsKeyboardFocusable` também veio
`false` e não pode servir como critério autoritativo.

**Correção:** manter a detecção por nome/AutomationId e o fallback
descendente; em seguida reconhecer o layout alternativo somente se
a URL observada corresponder a `/project` ou `/c/` persistente
em `chatgpt.com`, houver exatamente um `Document:ChatGPT`,
dois `Edit` visíveis/habilitados na janela (um da barra de endereço
identificada por nome/ID e um único `Edit` anônimo), e nenhum
`Edit` filho do documento. O pin de HWND/RuntimeId e a URL exata
continuam verificados na leitura passiva, e o campo precisa confirmar
o readback antes do primeiro envio.

`FindSendButton` também aceita **um único botão de envio nomeado** fora
do documento, apenas com URL válida e um `Document:ChatGPT` único.
Controles duplicados ou ausentes bloqueiam sem clique.

**Retomada após essa falha:** na implementação observada, a busca do
editor ocorre antes de reservar o próximo ID. A navegação de
`PRO_0001-GPTBOT_0001` para a rota `/project` pode já ter ocorrido na
mesma guia; o pin da conversa anterior é liberado sem excluir seu
cadastro. Verificar `pendingNewChats()` antes de repetir a operação.
Se `pending: []` e a guia vinculada ainda estiver em `/project`,
reexecutar `createProjectChat` **uma única vez** com o mesmo handoff
após atualizar a branch. Se houver reserva, resolver por reconciliação,
sem reenviar.

**Ainda não validado:** compilação/self-test da revisão no Windows,
foco do `Edit` anônimo, primeiro envio e registro automático de
`PRO_0001-GPTBOT_0002`. Não marcar continuidade entre conversas como
comprovada antes desse E2E.


## 18/11/2026 — Continuidade: falha de foco no preflight de envio

No commit local `d332528`, a recuperação de
`PRO_0001-GPTBOT_0001` comprovou `SetFocus` (URL/RuntimeId),
`getText()=""` e `isGenerating()=false`. Entretanto, a primeira
chamada `sendMessage()` retornou `BUSY_TAB_NOT_ACTIVE` e a segunda
retornou `EDITOR_EMPTY`.

Causa localizada: `Chat.RequireCanSend()` chamava
`ChatLimitDetector.Probe()` **antes** de reativar a guia. O detector
é passivo por definição; quando o terminal recupera o foco, ele bloqueia
antes de examinar o editor. O código de envio foi corrigido para executar
`SetFocus()` na guia previamente vinculada, verificar URL e pin,
e repetir **somente o preflight de limite**, até três tentativas
limitadas, se o motivo for `BUSY_TAB_NOT_ACTIVE`.

Isso não modifica `isGenerating()`, que continua passivo; tampouco
repete qualquer envio, clique, preenchimento, ou aceita mudança de
URL/RuntimeId. `EDITOR_EMPTY` continua bloqueando.

**Condição do teste:** a saída `getText()=""` foi observada antes das
duas tentativas. Portanto o segundo bloqueio `EDITOR_EMPTY` é
correto. Não concluir que a mensagem anterior chegou ou foi perdida
sem consultar visualmente o histórico. Depois de atualizar o executável,
validar a existência ou ausência dessa mensagem no histórico ANTES
de enviar qualquer novo texto.


## Diagnóstico de envio subsequente na mesma conversa — 18/11/2026

No teste da conversa `PRO_0001-GPTBOT_0001`, o comando
`sendMessage("TESTE_CONTINUIDADE_V7_1 ...")` retornou
`INPUT_DISPATCHED_UNCONFIRMED` com `textReadback=MATCHED`,
`inputSource=current-editor` e `humanTextAllowed=true`.
O usuário observou **texto preenchido, mas sem envio**. O código antigo
chamava `button.Click()` e devolvia um status sem verificar qualquer
mudança posterior; por isso, o retorno não comprovava a entrega.

O envio em conversa persistente agora:
- escolhe exatamente um botão de envio **dentro do documento web**;
- faz **uma única ação**: `InvokePattern` do botão se disponível;
  caso contrário, verifica novamente o editor, seu foco e a guia
  antes de pressionar **Enter uma única vez**;
- em `/project`, mantém o clique já comprovado do primeiro envio;
- verifica a identidade da mesma guia e o valor do compositor após
  a ação, por leituras limitadas e sem novas teclas ou cliques.

Os resultados diferenciam:
- `SEND_DRAFT_RETAINED_UNCONFIRMED`: texto continua no editor;
- `SEND_EDITOR_CLEARED_SERVER_UNCONFIRMED`: editor ficou vazio,
  mas a chegada da mensagem ao servidor ainda exige verificação;
- `SEND_TARGET_CHANGED_UNCONFIRMED`: mudou a guia/URL;
- `SEND_OBSERVATION_UNAVAILABLE`: UIA não comprovou o resultado;
- `SEND_EDITOR_CHANGED_UNCONFIRMED`: editor mudou de forma não esperada.

A saída inclui `dispatchAction`, `postActionUiReads` e
`postActionUiErrors`, além de `serverDeliveryConfirmed=false`.
Em todos os casos, um segundo envio automático é **proibido**.
Se o texto permanecer, primeiro inspecionar visualmente a conversa
antes de autorizar qualquer novo clique.

**Não confundir** a alteração publicada no GitHub com o executável
local: o usuário ainda tinha o commit `1657ade` devido a falhas
`Could not resolve host: github.com`. O E2E da nova versão está pendente.


## Validação de continuidade operacional (sem duplicar conversas)

A validação deve separar **persistência**, **envio subsequente** e
**handoff semântico**. Uma mensagem recebida no projeto não comprova,
por si só, que uma conversa nova recebeu o estado da anterior.

### Etapa A — Retomar uma conversa já cadastrada, sem criar outra

A segunda conversa do projeto `autonomous-cycle-test` já foi registrada
como `PRO_0001-GPTBOT_0001` e foi observada na URL persistente:

```text
https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260-autonomous-cycle-test/c/86cc7156-2120-15bf-982a-804aaa3ffd88
```

No console aberto (inclusive versão local antiga `1657ade`):

```text
use "PRO_0001-GPTBOT_0001"
use project "PRO_0001"
isGenerating()
getText()
```

Aguarde `isGenerating()=false` e editor vazio antes de enviar.
Executar **uma única vez**:

```text
sendMessage("TESTE_CONTINUIDADE_V7_1: esta é uma mensagem subsequente na conversa PRO_0001-GPTBOT_0001. Responda CONTINUIDADE_OK e repita o identificador da conversa.")
```

Confirmar a resposta na **mesma** guia/URL acima. O retorno
`INPUT_DISPATCHED_UNCONFIRMED` confirma somente a tentativa de clique;
não comprova recebimento da mensagem. Em `COMException`, não reenviar às cegas:
verificar a conversa já existente.

Depois consultar:

```text
web.projectListChats("PRO_0001")
pendingNewChats()
```

Aceitar se `PRO_0001-GPTBOT_0001` continuar `REGISTERED`,
`pendingNewChats()` ficar vazio e a resposta aparecer na mesma conversa.
Uma reinicialização do console seguida de `use` e `getText()` comprova
persistência de cadastro e disponibilidade do editor após reinício.

### Etapa B — Criar terceira conversa com handoff explícito

**Somente depois de atualizar com sucesso a branch** e confirmar no
preflight um commit recente (não `1657ade`). Com segunda conversa
selecionada e resposta finalizada:

```text
web.createProjectChat("PRO_0001","HANDOFF_V7_1: continuar o teste do projeto autonomous-cycle-test. Conversa anterior: PRO_0001-GPTBOT_0001. Estado anterior: duas conversas registradas, criação na guia do projeto com reconciliação após COMException. Objetivo atual: criar a terceira conversa, registrar URL definitiva e prosseguir aqui sem nova guia. Responda HANDOFF_RECEBIDO e o ID do predecessor.","Teste de continuidade entre chats")
```

Executar **uma única vez**. Se a intenção de primeiro envio estiver
`FIRST_SEND_ATTEMPTED`, nunca enviar novamente; usar exclusivamente
`web.reconcileProjectChat("PRO_0001-GPTBOT_0002")`.

Aceitar a criação técnica se `PRO_0001-GPTBOT_0002` tiver
`REGISTERED`, `projectId=PRO_0001`, URL persistente no mesmo projeto
e `pendingNewChats()` vazio. Aceitar a continuidade semântica somente
quando o novo chat responder corretamente ao handoff recebido; em seguida,
enviar uma segunda mensagem na mesma conversa e confirmar que o URL/ID
não mudaram.

**Limite atual:** a mensagem de handoff ainda é fornecida como argumento
explícito do comando; não há mecanismo comprovado de extração automática
de todo o histórico da conversa anterior. Um projeto compartilhado também
não é prova de transferência integral desse histórico. Uma migração autônoma
de contexto exige fonte autoritativa versionada, recibo do handoff e
reconciliação idempotente do primeiro envio.


### Recuperação do envio das 09h25 (18/11/2026)

O envio foi confirmado e o comando retornou `FIRST_SEND_URL_UNVERIFIED`
porque o ChatGPT apresentou uma rota com **slug no projeto** e um
identificador **provisório**:

```text
https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260-autonomous-cycle-test/c/local-chatgpt%3A1448ec50-9c6d-4f5f-a3e6-fc384140d045
```

A V7.1 distingue o ID opaco do projeto (32 hexadecimais) do seu slug
descritivo e reconhece `local-chatgpt%3A...` como **provisório**.
Esse endereço não pode ser salvo como URL definitiva em `chats.json`.

Para recuperar `PRO_0001-GPTBOT_0000` sem repetir o primeiro envio:

```text
web.reconcileProjectChat("PRO_0001-GPTBOT_0000")
```

A reconciliação monitora apenas a guia de HWND/RuntimeId persistidos.
Se observar uma URL remota definitiva `/c/...` no mesmo projeto,
conclui automaticamente o cadastro e seleciona a conversa.
Se observar somente a rota provisória, retorna
`FIRST_SEND_PROVISIONAL_URL` e preserva a reserva
`FIRST_SEND_ATTEMPTED`. O comando de reconciliação **não envia mensagens**
e não abre nova guia.

### 18/11/2026 — Exceção UIA 0x80040201 na segunda conversa

A mensagem da segunda conversa foi recebida, mas o console executava
ainda o commit `1657ade`, porque `git pull` falhou com
`Could not resolve host: github.com`. O preflight e o self-test
PASS referiam-se **à versão antiga**, não à correção do editor.

A UIA retornou `COMException: 0x80040201` ("Um evento não pôde chamar
assinantes"). A exceção isolada não demonstra em qual fase o envio falhou.
O contador `nextChatNumber=2` e o pin anterior nulo mostram que
a segunda reserva provavelmente foi criada e a guia anterior reutilizada,
mas não confirmam que `PRO_0001-GPTBOT_0001` tenha sido cadastrado.

**Não repetir o comando de criação.** No console aberto:

```text
pendingNewChats()
web.projectListChats("PRO_0001")
```

Se a reserva for `FIRST_SEND_ATTEMPTED`, a versão anterior já permite
`web.reconcileProjectChat("PRO_0001-GPTBOT_0001")`, sem envio. Após
atualizar a branch, a reconciliação aceita também reservas
`TAB_CREATED` e `OPENED_PENDING_URL`: nesse caso só registra quando
a mesma guia/pin exibe uma URL remota definitiva dentro do projeto e
a URL não colide com um chat já cadastrado.

Em erro UIA 0x80040201, a nova versão preserva a reserva, apresenta
o `HRESULT` e o estado persistido, e repete somente leituras limitadas
na guia fixada. **Nunca repete teclado, clique ou envio.** Uma chamada
posterior a `createProjectChat` diante de reserva anterior passa
a ser exclusivamente de reconciliação (sem reenviar).

### V7.1 — Editor sem nome na página /project

O teste da segunda criação (commit `1657ade8`) compilou e passou no
self-test, mas retornou `PROJECT_COMPOSER_NOT_UNIQUE` com
`VisibleEditors=[Edit:Barra de pesquisa e endereços#view_1017 |
Document:ChatGPT#RootWebArea | Edit:#]` e **nenhum envio**.

O fallback atualizado não depende mais de rótulo visível
`Novo chat em <projeto>` nem de `IsKeyboardFocusable`, porque ambos
podem estar ausentes na árvore UIA. Ele só reconhece um `Edit` sem nome/ID
quando a URL selecionada é exatamente a página `/project`, há
**um único Document:ChatGPT** e existe **um único Edit visível/habilitado
dentro desse documento**. A barra de endereços do Edge, fora do documento,
não é candidata. A validação de `HWND`, `RuntimeId`, foco e readback
continua obrigatória antes de qualquer envio.

Como o erro ocorreu antes da criação da reserva, a operação pode ser
repetida com o mesmo comando após atualização da branch. Se uma
reserva passar a existir, recuperar pelo mesmo ID, sem duplicar mensagem.

### V7.1 — Seleção coerente de projeto e conversa

O console possui **um único contexto de seleção**. Selecionar uma
conversa com `use "PRO_0001-GPTBOT_0001"` seleciona também
`PRO_0001`, obtido do `ChatRecord.ProjectId` ou, para chats
legados, da URL comprovada contra os projetos cadastrados.

O prompt passa a indicar:

```text
autonomous-cycle-test[PRO_0001-GPTBOT_0001|PRO_0001]>
```

O comando `use project None` desmarca a conversa caso ela pertença
a um projeto. Selecionar outro projeto também desmarca uma conversa
de contexto incompatível, **sem apagar nenhum chat do catálogo**.
Selecionar uma conversa comum remove a seleção anterior de projeto.

`web.projectListChats("PRO_0001")` **consulta** o projeto,
mas não muda a seleção. A saída separa `queriedProject` (projeto
consultado) de `selectedProject` (projeto efetivamente selecionado)
e informa `selectedChat`. Inicialização com `None[-|None]`
continua normal quando nenhum chat/projeto foi selecionado na sessão.

### V7.1 — Segunda conversa na mesma guia (18/11/2026)

Após `PRO_0001-GPTBOT_0000` ser registrado, a guia original saiu de
`/project` e passou a `/c/...`. O comando `createProjectChat`
anterior retornava `PROJECT_COMPOSE_PAGE_NOT_SELECTED` por exigir
que o operador retornasse manualmente à página de composição.

A criação agora **primeiro procura uma guia /project do projeto**.
Se não houver, reaproveita o **chat registrado selecionado com USE**
quando pertencer ao projeto, usando o HWND e RuntimeId persistidos.
Sem USE, aceita apenas **uma única guia já selecionada no Edge**
cuja URL, janela e RuntimeId correspondam a um chat do mesmo projeto
existente no catálogo. Não escolhe por índice, título ou slug.

Antes de navegar automaticamente para `/project`, verifica que a
conversa anterior **não está gerando resposta** e que seu compositor
**não contém rascunho**. A URL histórica do chat antigo permanece
registrada, mas seu pin obsoleto é removido após confirmar o
compositor da página do projeto. O mesmo HWND/RuntimeId é usado para
enviar a nova mensagem, observar a URL definitiva e registrar o novo chat.

Teste recomendado a partir de `PRO_0001-GPTBOT_0000` selecionado:

```text
web.createProjectChat("PRO_0001","Segunda conversa para validar criação e cadastro em uma única operação","Teste automático V7.1")
```

**Atenção:** esse comando envia uma mensagem real. Não executar outra
vez se houver reserva com `FIRST_SEND_ATTEMPTED`; nesse caso usar
`web.reconcileProjectChat("PRO_0001-GPTBOT_0001")`.
A compilação e o E2E dessa correção ainda precisam ser validados no
Windows com UIA.

## V7.1 — Criar conversa na própria guia do projeto

O comando recomendado para projetos usa a página já aberta em `/project` e **não abre outra guia**:

```text
web.createProjectChat("PRO_0001","Mensagem inicial de teste","Teste de associação")
```

O comando acima **envia uma mensagem REAL**. Não o execute para inspeção.
Ele usa o `HWND` e `RuntimeId` da guia selecionada; o primeiro envio transforma
`/g/g-p-ID/project` em `/g/g-p-ID/c/ID_CHAT`, permanecendo na mesma guia.
Depois de observar a URL final e confirmar seu escopo, cadastra o ID interno
`PRO_0001-GPTBOT_0000`, o título e a URL em `chats.json`, selecionando o chat
no console. Se o ChatGPT não confirmar a URL, preserva o estado
`FIRST_SEND_ATTEMPTED`; uma chamada repetida com os mesmos argumentos tenta
**somente finalizar o registro**, sem reenviar. A implementação espera por um
período limitado, sem abrir guias adicionais.

Se já houver uma reserva pendente no projeto (inclusive `TAB_CREATED` do fluxo
V7.0), o comando a reutiliza, em vez de consumir outro contador. A recuperação
prioriza o pin anterior e verifica o compositor `/project` antes do envio. Se
a reserva estiver ambígua ou associada a outra URL, bloqueia sem criar chat.
Se não houver reserva, procura `/project`; alternativamente, navega
a própria guia de uma conversa já registrada do projeto (identificada pelo
`USE` ou por seleção UIA única), desde que não esteja gerando resposta
nem contenha rascunho. Nenhum ID é reservado antes de comprovar
`/project` e seu compositor.

**Importante:** a versão V7.1 tem testes puros de parser e identificação de
rótulos UIA; o E2E real no Edge precisa ser executado localmente. Até comprovar
o primeiro envio e a URL persistente, não considerar o chat registrado.

# V7.0 — Projetos, GPTBOT autonumérico e novas conversas

## Modelo

`Project` representa um projeto previamente cadastrado, com `Id` (`PRO_0000`), `FullName`, `UrlPrefix` e `ProjectListChats()/projectListChats()`. `Project.RegisteredChats` mantém um *snapshot estático* dos resultados consultados; `projects.json` e `chats.json` são a fonte persistente.

`use "chat00"` continua selecionando o chat. `use project "PRO_0000"` seleciona o projeto **separadamente**, sem alterar o chat. `use project None` redefine o contexto de projeto para o estado inicial **None**. Nenhuma seleção é persistida entre sessões.

O prompt do console mostra o **nome completo cadastrado** do projeto, seguido dos identificadores de chat e projeto: `RM0 — MA GPTBOT Base Capabilities[GPTBOT_0001|PRO_0000]>`. Sem seleção de projeto, o prefixo é `None`: `None[chat00|None]>` ou `None[-|None]>`. O nome do projeto não é inferido da URL: vem do cadastro. O comando `use "GPTBOT_0001"` seleciona automaticamente seu projeto cadastrado.

Nomes:

- Sem projeto: `GPTBOT_0009`, `GPTBOT_0010` ... O início em 0009 leva em conta que `GPTBOT_0008` já foi utilizado.
- Com projeto: `PRO_0000-GPTBOT_0000`, `PRO_0000-GPTBOT_0001` ... Cada projeto tem contador separado.
- `Chat.DefaultIdPrefix` e `Chat.InitialStandaloneSequence` são constantes estáticas; as sequências correntes estão gravadas em `%LOCALAPPDATA%\ChatAutomation\projects.json`.
- Contadores e reservas são alterados sob mutex cross-process. Um nome **não** é reutilizado após uma abertura inconclusiva, evitando criar duas conversas sob o mesmo ID.
- `ChatRecord.ProjectId` e `ChatRecord.DisplayTitle` são persistidos em `chats.json` quando a URL definitiva for confirmada.
- Chats antigos capturados com `web.captureChat` ou `web.addChat` são reconhecidos retroativamente por `UrlPrefix`. Novos cadastros nessas rotas passam a gravar `ProjectId` quando o projeto já existe; uma URL fora desse escopo não é atribuída ao projeto.

## Exemplo no console

Cadastrar o projeto usando o chat já conhecido (recomendado; **não precisa digitar URL**):

```text
web.addProjectFromChat("RM0 — MA GPTBOT Base Capabilities","GPTBOT_0001")
listProjects()
use project "PRO_0000"
web.previewNewChat()
projectListChats()
```

Alternativa: `web.addProject("Nome completo","https://chatgpt.com/g/g-p-...")`. A URL deve preservar `https://` e os separadores `/`.

O editor interativo não descarta caracteres imprimíveis produzidos por AltGr (Ctrl+Alt em alguns layouts). `Ctrl+V` e `Shift+Insert` inserem o texto Unicode da área de transferência como **uma única linha**; nenhum Enter colado executa comandos automaticamente.

Com título descritivo:

```text
web.addNewChat("Nova etapa")
web.addNewChat(Project)
web.addNewChat("Revisão", Project)
web.addNewChat("Tarefa explícita", "PRO_0000")
```

Voltar a criar fora de projetos:

```text
use project None
web.addNewChat()
web.addNewChat("Pesquisa isolada")
```

Os chats previamente cadastrados continuam acessíveis pelo mesmo `use "chat00"`, sem interferência dos projetos.

## Em C#: sobrecargas tipadas

```csharp
using var web = new WebInterface();
Project projeto = web.AddProject("Nome completo", "https://chatgpt.com/g/g-p-...");

web.UseProject(projeto);
NewChatResult a = web.AddNewChat();
NewChatResult b = web.AddNewChat("Nome do chat");
NewChatResult c = web.AddNewChat(projeto);
NewChatResult d = web.AddNewChat("Nome do chat", projeto);

web.UseProject((string?)null);  // None
NewChatResult e = web.AddNewChat();
```

## Pré-ativação (primeira instalação)

A V6.8 é o último código instalado. A V7.0 será a primeira versão com catálogo de projetos: **não requer migração de identificadores de projetos**. O formato inicial é `PRO_0000-GPTBOT_0000`, com prefixo estático `Project.DefaultIdPrefix = "PRO_"`.

No PowerShell 7, na branch `feat/projects-new-chat-registry`:

```powershell
& '.\\activate-v7.ps1'
```

O script exige checkout limpo e a branch correta; executa `setup.ps1` e imprime `PASS_COMPILE_AND_SELFTEST` sem abrir o Edge, reservar IDs ou enviar mensagens. Para iniciar o console após o PASS:

```powershell
& '.\\activate-v7.ps1' -LaunchConsole
```

A prévia de nomes também não tem efeitos:

```text
use project None
web.previewNewChat()
web.addProject("Projeto teste","https://chatgpt.com/g/g-p-...")
use project "PRO_0000"
web.previewNewChat()
web.previewNewChat("Título", Project)
```

A prévia não garante que outro processo não consuma o próximo identificador antes de `addNewChat`.

### Detectar automaticamente o nome do projeto pela UIA

Com a página `/project` já selecionada no Edge, sem ter cadastrado o
projeto, execute:

```text
web.previewProjectFromEdge()
```

O programa consulta a URL selecionada, extrai o nome visível de `Novo chat em
<nome>` (ou `New chat in <name>`) e o confronta com um controle Text
visível que contenha exatamente o título do projeto. Exibe `PREVIEW_ONLY`
com `fullName`, `projectUrl` canônica, `observedUrl`, `placeholderText`,
`headingMatched`, `composerVerified`, `windowHandle` e `tabRuntimeId`.

Exemplo de saída esperada na página de teste:
`fullName = "autonomous-cycle-test"`,
`projectUrl = "https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260"`.
**Não** é um valor estático no programa: vem da UIA naquele instante. Se
nenhum placeholder único puder ser encontrado, retorna erro explícito e
mantém o cadastro intocado — nunca inventa o nome pelo slug da URL.

Depois de conferir o nome, execute:

```text
web.addProjectFromEdge()
```

A confirmação relê a página e compara nome, placeholder, URL exata e
`RuntimeId` com a prévia **da mesma sessão**. Se houver divergência,
solicita nova prévia, sem gravar. O registro usa o nome extraído e a raiz
canônica do projeto; não seleciona o projeto ou cria chat automaticamente.
`web.addProject("Nome","URL")` continua disponível como alternativa manual.

### Criar conversa em projeto vazio pela página /project

Projetos novos do ChatGPT podem disponibilizar o campo de mensagem diretamente em
`https://chatgpt.com/g/g-p-ID/project`. O programa aceita essa URL no cadastro
(`web.addProject("NOME EXATO", "https://chatgpt.com/g/g-p-ID/project")`) e armazena
a identidade canônica `https://chatgpt.com/g/g-p-ID`. Não é preciso adicionar
previamente nenhum chat ao projeto.

Depois do cadastro, selecione o projeto pelo ID retornado. Para verificar que o
compositor existe, mantenha a página `/project` selecionada no Edge e execute
`inspectProjectNewChat()` (somente leitura). A criação pode então ser iniciada por
`web.addNewChat("Título de teste", Project)`.

- A reserva de nome é feita sob mutex e a guia nova navega diretamente para
  `/project`, **não** para a URL canônica que pode redirecionar a um chat antigo.
- Só retorna `OPENED_PENDING_URL` após comprovar a mesma guia (`HWND/RuntimeId`),
  a URL exata da página do projeto e um único editor UIA acessível.
- Nenhum clique em `Novo chat` e **nenhuma mensagem automática** nessa etapa.
  O primeiro envio **explícito** cria a conversa remota no projeto. A URL deve
  mudar para `/g/g-p-ID/c/...`.
- O envio pode ser humano na guia identificada ou efetuado pelo comando tipado
  `web.sendFirstNewChatMessage("ID_RESERVADO","Mensagem de teste")`.
  Essa chamada exige `OPENED_PENDING_URL` originado de `/project`, a guia
  fixada por `HWND+RuntimeId` e o editor vazio. Prepara a mensagem com
  timestamp UTC−3, verifica o readback e persiste `FIRST_SEND_ATTEMPTED`
  antes de clicar em Enviar. **Pode efetuar envio REAL; use somente com autorização.**
- Se a resposta for `INPUT_DISPATCHED_UNCONFIRMED`, o clique foi tentado,
  mas isso não comprova entrega. Não repetir o comando: primeiro verificar a
  URL da mesma guia. Se falhar após registrar `FIRST_SEND_ATTEMPTED`, o envio
  pode ou não ter ocorrido; o programa nunca dispara a mesma mensagem outra vez.
- Após surgir `/c/...`, `web.completeNewChat("ID_RESERVADO")` valida o mesmo
  `RuntimeId`, o escopo do projeto e a URL persistente, então registra a conversa.
- Se a navegação não comprovar o editor, mantém a reserva pendente. Não criar
  outra reserva antes de resolver esse estado.

Exemplo para a URL de projeto vazio fornecida no teste:

```text
web.addProject("NOME EXATO DO PROJETO", "https://chatgpt.com/g/g-p-d3b62d37c2f0d39ccdbb171f4c929260/project")
use project "PRO_0001"
web.previewNewChat("Teste de associação", Project)
```

Depois de conferir a prévia, `web.addNewChat("Teste de associação", Project)`
abre o compositor em outra guia e retorna `OPENED_PENDING_URL` sem enviar.
Para criar a conversa remota explicitamente, use o ID reservado retornado:
`web.sendFirstNewChatMessage("PRO_0001-GPTBOT_0000","Teste operacional de criação no projeto")`.
Depois que o ChatGPT exibir `/c/...` na mesma guia,
`web.completeNewChat("PRO_0001-GPTBOT_0000")` registra a URL.
Não envie se a operação de abertura falhou ou se não confirmou o editor.

`PRO_0001` é o ID esperado se só existir o cadastro `PRO_0000`; use o ID
efetivamente retornado por `web.addProject` se for diferente.

### Identificar a ação real de nova conversa (sem efeitos)

Antes de criar outra reserva, inspecione os controles UIA **de uma guia já existente no projeto**. Exemplo com o projeto `PRO_0000` e seu chat `GPTBOT_0001`:

```text
use project "PRO_0000"
use "GPTBOT_0001"
setFocus()
inspectProjectNewChat()
```

`setFocus()` apenas seleciona a conversa existente, sem envio. `inspectProjectNewChat()` é **somente leitura**, e exige URL exata da raiz do projeto ou uma URL persistente de conversa já associada; não seleciona aba, não navega, não reserva identificador e não envia. O resultado `INSPECTED_READONLY` informa:

- `observedUrl`, `windowHandle` e `tabRuntimeId` da guia efetivamente selecionada;
- `projectNameVisible`, indicando se há texto UIA com o nome cadastrado do projeto;
- `controls`: botões, hyperlinks ou itens de menu relacionados a criar/iniciar chats ou projetos, com `name`, `automationId`, `helpText`, `insideDocument`, `invokeSupported` e `parentChain` (hierarquia UIA);
- confirmação de URL e RuntimeId após a leitura; mudanças invalidam o resultado.

Se não houver uma guia já selecionada do projeto, retorna `PROJECT_INSPECT_TAB_NOT_SELECTED`, sem fazer alterações. Se houver múltiplas janelas do projeto selecionadas, retorna `PROJECT_INSPECT_AMBIGUOUS`. Não cria um novo chat só para diagnosticar o botão.

**Não usar `web.addNewChat()` enquanto o seletor real ainda não estiver comprovado no Edge local.** A URL raiz já redirecionou para um chat antigo em teste anterior, portanto navegar apenas à raiz não comprova a criação.

### Regras de segurança do primeiro teste UIA

- Fora de projeto, a nova guia deve mostrar compositor de novo chat em `https://chatgpt.com/`; se a raiz redirecionar a uma conversa existente, a operação bloqueia.
- Dentro de projeto, a rota **`/project`** é a página preferencial de composição. Se ela apresentar editor UIA comprovado, nenhum botão de nova conversa precisa ser acionado. Se não apresentar editor e redirecionar a um chat existente, o programa não pode tratá-lo como conversa nova; ações específicas do projeto ainda exigem prova UIA.
- Depois da criação, a operação retorna `OPENED_PENDING_URL` e não envia texto. O identificador remoto é capturado por `web.completeNewChat("PRO_0000-GPTBOT_0000")` depois que houver URL persistente.
- Se não encontrar a ação UIA específica do projeto ou não conseguir comprovar a guia, preserva a reserva e retorna erro. Não repete cliques nem inventa uma URL.

### Retomar uma abertura interrompida sem consumir outro ID

Se `addNewChat` retornar `NEW_CHAT_OPEN_UNCERTAIN`, consulte `pendingNewChats()`. A reserva e seu título continuam persistidos. Exemplo da primeira execução:

```text
use project "PRO_0000"
pendingNewChats()
web.resumeNewChat("PRO_0000-GPTBOT_0000")
# Se pin ausente e GUIA ORIGINAL CONFIRMADAMENTE FECHADA:
web.reopenClosedNewChat("PRO_0000-GPTBOT_0000")
pendingNewChats()
projectListChats()
```

- **`RESERVED` e sem pin:** a tentativa antiga não guardou a identidade da guia; `resumeNewChat` abre uma nova guia reutilizando o nome reservado, **sem** consumir `GPTBOT_0001`. A guia antiga pode permanecer aberta, mas não é adotada sem provas.
- **Guia fechada pelo usuário:** `web.reopenClosedNewChat("PRO_0000-GPTBOT_0000")` usa a **mesma reserva** e abre outra guia de forma explícita, após inspecionar as guias do Edge e confirmar que o `RuntimeId` antigo não está presente. Este é o caminho para o caso observado de `TAB_CREATED` com `pinnedMatches=0`. A verificação de ausência não prova, por si só, que a guia foi fechada: **o comando expressa a confirmação do usuário**.
  - Aceita somente `TAB_CREATED` (nenhum CTA de nova conversa do projeto registrado), com `windowHandle` e `tabRuntimeId` originais. Bloqueia se o pin ainda existir ou se uma janela Edge não puder ser inspecionada.
  - Persistência: `REOPEN_REQUESTED` → `REOPEN_ATTEMPT_STARTED` (antes de `Ctrl+T`) → `TAB_CREATED` (novo HWND/RuntimeId). Mantém `PRO_0000-GPTBOT_0000`, o título, `PreviousWindowHandle`, `PreviousTabRuntimeId` e `ReopenCount`. **Não** avança `NextChatNumber`.
  - Se a execução parar entre `Ctrl+T` e a captura do novo pin, o estado `REOPEN_ATTEMPT_STARTED` impede abrir outra guia cegamente. É necessária reconciliação explícita, não uma repetição do comando.
- **`TAB_CREATED` com HWND/RuntimeId:** a retomada reutiliza **a mesma guia**, sem abrir outra. A identidade é persistida imediatamente após comprovar `Ctrl+T`, antes da navegação.
  - No início de `resumeNewChat` ou antes do teclado inicial, a recuperação pode selecionar **exclusivamente o RuntimeId persistido** e confirma essa seleção antes de agir.
  - **Depois de Enter, o monitor é somente leitura**: se o usuário mudar de janela **ou de aba** (inclusive dentro do Edge), retorna `PAUSED_FOCUS_CHANGED` sem trazer o Edge para frente ou selecionar a guia original. A reserva continua com o pin guardado.
  - Se o pin desaparecer, a retomada bloqueia com `NEW_CHAT_RESUME_PIN_UNVERIFIED` e inclui diagnóstico somente leitura (`tabCount`, `pinnedMatches`, `selectedRuntimeId`, `expectedRuntimeId`). Não escolhe índice, URL semelhante ou outra aba.
  - Durante uma nova abertura, a seleção do pin é verificada antes de digitar o endereço e novamente antes de confirmar com Enter, evitando navegação para outra guia.
- **Perda de foco durante navegação:** o monitor pode continuar lendo passivamente URL e compositor quando `HWND + RuntimeId` permanecem selecionados, sem `SetForegroundWindow`, teclado ou clique. Se identificar o editor da nova conversa, conclui `OPENED_PENDING_URL` mesmo que o Edge esteja em segundo plano.
  - Se a página precisar de um CTA específico do projeto ou outra guia for selecionada fora de foco, retorna `PAUSED_FOCUS_CHANGED`, **não** `NEW_CHAT_OPEN_UNCERTAIN`, preservando `TAB_CREATED` ou `PROJECT_ACTION_ATTEMPTED`, inclusive o pin `42.1180792.4.0.0.183231` registrado no teste.
  - `PAUSED_FOCUS_CHANGED` não registra uma URL nova, não cria guia nem envia mensagem. A retomada usa `web.resumeNewChat("PRO_0000-GPTBOT_0000")`, que verifica o mesmo RuntimeId.
  - O Edge só precisa assumir foco para navegar, selecionar guia ou invocar o CTA de criação. A simples observação não deve alterar o foco do usuário.
- **`PROJECT_ACTION_ATTEMPTED`:** a ação explícita de nova conversa no projeto foi registrada **antes** da invocação UIA. A retomada NÃO clica novamente; apenas verifica a mesma guia, a alteração da URL e o compositor. Caso não seja possível confirmar, mantém a reserva e informa os controles visíveis.
- **`OPENED_PENDING_URL`:** a retomada apenas relata a guia já confirmada; após enviar a primeira mensagem explicitamente, usar `web.completeNewChat("ID")`.
- Operações concorrentes sobre a mesma reserva são serializadas por mutex nomeado. Tentativa de mudar o pin já gravado, repetir o CTA ou substituir uma URL existente é bloqueada.
- Se `https://chatgpt.com/g/g-p-...` redirecionar a uma conversa existente do **mesmo** projeto, isso não é evidência de nova conversa. O programa procura uma ação UIA explicitamente identificada como *new chat in project*/*novo chat no projeto*. Nunca aciona o botão genérico de chat novo da barra lateral.
- Em erro de detecção, `PROJECT_NEW_CHAT_ACTION_NOT_FOUND` inclui rótulos dos botões de chat/projeto visíveis na página (`CandidateActions`). Essa evidência permite adaptar o seletor ao Edge real sem escolher controles arbitrários. A recuperação não envia prompt.

`web.resumeNewChat` reutiliza a guia existente; `web.reopenClosedNewChat` é a reabertura explícita de uma guia fechada. **Não** repetir `addNewChat` para o mesmo objetivo, pois essa chamada sempre reserva o próximo ID.

## URL provisória versus URL definitiva

Abrir uma nova guia e carregar o compositor não garante que o ChatGPT já tenha atribuído um identificador de conversa `/c/...` ou `/uc/...`.

Por isso, `web.addNewChat(...)`:

1. **Reserva** o nome e a associação de projeto em `projects.json` (persistente).
2. Abre outra guia e navega para `https://chatgpt.com/` ou o `UrlPrefix` do projeto. Exige prova de nova guia, mesmo `RuntimeId`, URL no escopo e editor UIA acessível.
3. Devolve `OPENED_PENDING_URL` após comprovar a nova guia e o editor. Nunca cadastra imediatamente uma URL que poderia pertencer a uma conversa anterior; a finalização é explícita.
4. **Não envia mensagens**. Após o primeiro envio explícito do usuário no navegador, execute `web.completeNewChat("GPTBOT_0009")` ou o identificador do projeto; ele comprova que a guia original agora tem URL persistente e finaliza o registro.
5. Em abertura inconclusiva, retorna `NEW_CHAT_OPEN_UNCERTAIN` e mantém a reserva. Consulte `pendingNewChats()` antes de qualquer nova tentativa. Não há repetição automática de cliques ou envios.

A rotina `CompleteNewChat` aceita apenas URLs persistentes dentro do escopo e recusa colisões de nome/URL sob o mutex do catálogo. Não confunde projeto, chat ou janela de origem.

### Testes locais sem envio

```powershell
& '.\setup.ps1'
& '.\chat-automation.ps1'
```

Valide `use project None`, cadastro, listagem, `Tab` sobre `use project P` e `web.addNewChat`. No primeiro teste real, tenha o Edge aberto com uma janela conhecida. Para projetos, verifique se a URL raiz do projeto apresenta um compositor; caso o Edge redirecione para outra rota sem editor, a operação deve falhar de modo explícito **sem registrar uma conversa fictícia**.

O `self-test` cobre sequência global a partir de 0009, sequência independente por projeto, escrita persistente, detecção de URL, listagem de reservas, nome descritivo, seleção `None`, colisões e parser do console. A criação real da guia requer teste Windows/Edge.

## Dependências

Esta branch deriva de `refactor/use-single-context` (PR #5). Os PRs #1–#5 permanecem empilhados; não atualizar a `main` antes dos testes.
