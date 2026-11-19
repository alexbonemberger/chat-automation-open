## FullName do chat — nome visível na interface (V7.1, pré-consolidação)

**Revisão pré-consolidação:** [docs/review-2026-11-18.md](docs/review-2026-11-18.md). O último E2E local confirmado é anterior às correções dessa auditoria; não considerar a branch consolidada sem nova compilação e self-test no Windows.

Cada conversa tem um identificador estável, como
`PRO_0001-GPTBOT_0003`, e pode ter um `fullName` diferente,
por exemplo `Continuidade de sistemas`. `fullName` não
substitui `Name`, `displayTitle` nem URL/RuntimeId.

Após `web.createProjectChat` concluir a geração e registrar
URL, a aplicação tenta capturar o nome da interface por UIA:
lê o título do `TabItem` da guia selecionada, validando a
URL e o `RuntimeId` antes/depois. Remove o sufixo de
telemetria do Edge, por exemplo ` - Uso de memória - 597 MB`.
Se houver também título visível em controle da página,
usa `UIA_TAB_AND_PAGE`; caso contrário, grava a origem
`UIA_TAB_VERIFIED` sem inferir o nome da lista de Recentes.
`web.captureChat("ID")` também tenta capturar o título
da guia ativa.

Para um chat já registrado, no console:

```text
use "PRO_0001-GPTBOT_0003"
setFocus()
inspectFullName()
captureFullName()
getFullName()
listChats()
```

Se a UIA não confirmar, defina somente no catálogo local:

```text
setFullName("Continuidade de sistemas")
```

O prompt do console exibe `fullName` somente quando cadastrado
e omite visualmente o prefixo `PRO_0001-` do ID do chat
quando o mesmo projeto já aparece no final:

```text
autonomous-cycle-test[GPTBOT_0003 - Continuidade de sistemas|PRO_0001]>
autonomous-cycle-test[GPTBOT_0004|PRO_0001]>
```

A abreviação é somente visual. O cadastro, comandos `use`,
URLs e identificadores persistentes continuam usando
`PRO_0001-GPTBOT_0003`; projetos divergentes não são abreviados.

`setFullName` não renomeia o chat no site. Os campos
`fullName` e `fullNameSource` persistem em `chats.json`
e aparecem nas listagens do projeto. A operação de captura
não envia mensagens nem cria conversas.


## Diagnóstico de envio subsequente na mesma conversa — 18/11/2026

No teste da conversa `PRO_0001-GPTBOT_0001`, o comando
`sendMessage("TESTE_CONTINUIDADE_V7_1 ...")` retornou
`INPUT_DISPATCHED_UNCONFIRMED` com `textReadback=MATCHED`,
`inputSource=current-editor` e `humanTextAllowed=true`.
O usuário observou **texto preenchido, mas sem envio**. O código antigo
chamava `button.Click()` e devolvia um status sem verificar qualquer
mudança posterior; por isso, o retorno não comprovava a entrega.

O envio em conversa persistente agora:
- escolhe exatamente um botão de envio dentro do documento web ou, no layout
  UIA achatado do Edge, um botão único e verificado por URL e documento ChatGPT;
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

# Projetos e criação de chats (V7.0)

A classe `Project` mantém `Id`, nome completo, prefixo de URL e `projectListChats()`. A seleção de chat e projeto é sincronizada e começa como **None** na sessão. A classe `Chat` define o prefixo estático `GPTBOT_`; o próximo identificador global é `GPTBOT_0009` e os contadores ficam em `projects.json`.

```text
web.addProjectFromChat("RM0 — MA GPTBOT Base Capabilities","GPTBOT_0001")
use project "PRO_0000"
projectListChats()
web.previewNewChat("Etapa atual", Project)
web.addNewChat("Etapa atual", Project)
pendingNewChats()
use project None
web.addNewChat()
```

O `web.addProjectFromChat("Nome","GPTBOT_0001")` recupera a URL do chat já cadastrado, sem necessidade de colar URLs longas. O console também aceita caracteres gerados por AltGr e colagem Ctrl+V/Shift+Insert; apenas Enter executa comandos.

Se a criação falhar após reservar `PRO_0000-GPTBOT_0000`, use `pendingNewChats()` e `web.resumeNewChat("PRO_0000-GPTBOT_0000")` para recuperar **o mesmo identificador**, sem avançar o contador. Se a guia nova tiver sido fechada, use **explicitamente** `web.reopenClosedNewChat("PRO_0000-GPTBOT_0000")`; o sistema exige estado `TAB_CREATED`, pin antigo ausente, inspeciona as janelas Edge e preserva a mesma reserva. O programa fixa `HWND + RuntimeId` imediatamente após criar a guia e aceita redirecionamento para conversa antiga do mesmo projeto apenas como ponto de partida para encontrar um CTA de **nova conversa no projeto** — nunca como prova de nova conversa.

Para cadastrar um projeto já aberto no Edge sem digitar seu nome, use
`web.previewProjectFromEdge()` (nome extraído do placeholder `Novo chat em ...`
e conferido com o título) e, após validar a prévia,
`web.addProjectFromEdge()`. Ambos revalidam a mesma guia UIA; a prévia
não grava e a confirmação rejeita nome/URL/`RuntimeId` alterados. Se
o nome não estiver acessível, o fluxo bloqueia sem tentar derivá-lo da URL.

Projetos vazios com URL `https://chatgpt.com/g/g-p-ID/project` são aceitos diretamente por `web.addProject("Nome","URL")`. O cadastro guarda a raiz canônica; novas conversas são preparadas na página `/project`, que tem compositor próprio. O programa não envia a primeira mensagem automaticamente: `web.sendFirstNewChatMessage("ID","Texto")` é uma operação **explícita de envio REAL**, permitida apenas depois de confirmar o compositor /project vazio e seu RuntimeId. A primeira tentativa fica registrada para impedir reenvio acidental. Após surgir a URL persistente `/c/...` na mesma guia, `web.completeNewChat("ID")` registra a conversa. Não é necessário registrar manualmente chats antigos do projeto para criar o primeiro.

A investigação do fluxo real de criação no Edge usa `inspectProjectNewChat()` com `use project "PRO_0000"` e uma guia existente do projeto já selecionada. Essa consulta não reserva nomes nem interage com a página. Ela retorna os controles UIA candidatos e sua hierarquia para validar o seletor do botão de nova conversa. Consulte [docs/projects-new-chat.md](docs/projects-new-chat.md) antes de tentar outra criação.

O `web.addNewChat()` abre uma nova guia e reserva um nome. Se o ChatGPT ainda não fornecer URL definitiva, retorna `OPENED_PENDING_URL` e **não** cria um registro fictício em `chats.json`. Após enviar a primeira mensagem explicitamente, use `web.completeNewChat("GPTBOT_0009")` (ou o nome de projeto) para confirmar a mesma guia, capturar `/c/...` e consolidar o cadastro. Não há envio automático.

Pré-ativação no PS7: `& '.\\activate-v7.ps1'` (compila e testa, sem modificar o Edge). Para iniciar o console após o teste: `& '.\\activate-v7.ps1' -LaunchConsole`. A V6.8 continua intacta.

Documentação técnica, sobrecargas C#, fluxo de criação e recuperação: [docs/projects-new-chat.md](docs/projects-new-chat.md).

# Contexto único com `use` (V6.8)

A partir da V6.8, `use "nome"` é o **único seletor de contexto** do console interativo. `web.addChat("nome","url")` cadastra uma URL conhecida; `web.captureChat("nome")` cadastra a guia atualmente selecionada no Edge por UIA. Nenhuma das duas operações de cadastro altera o `use` vigente. `setFocus()` e `open()` não atualizam mais `CurrentBinding` global.

O antigo `bindToChat()` foi descontinuado **no console interativo**. Para registrar uma guia use `web.captureChat("nome")`, e para selecioná-la use `use "nome"`. Os scripts e APIs legados continuam disponíveis temporariamente para compatibilidade, mas usam um estado separado e não devem controlar o contexto do console moderno. Veja [docs/use-single-context.md](docs/use-single-context.md).

```text
web.captureChat("chat00")
use "chat00"
getText()
```

## Monitoramento do limite de conversa (UIA)

O aviso de duração máxima é detectado sem interações na página. O detector exige **texto de limite** e o botão acessível **Iniciar novo chat** na mesma conversa, identificada por URL exata + RuntimeId da guia. O estado booleano `Chat.IsAtMaxLimit`/`chat.isAtMaxLimit` começa em `false`, é gravado em `chats.json` após confirmação e permanece `true` para a URL atingida. Quando `true`, `sendMessage()` é bloqueado.

Com a guia do GPTBOT_0001 aberta no Edge:

```text
web.addChat("GPTBOT_0001","https://chatgpt.com/g/g-p-caaca3769873860ca7b78979d56ef499-rm-0-ma-gptbot-base-capabilities/c/1d7a0124-91da-6bbe-d5e3-fec6ec8936cc")
use "GPTBOT_0001"
isAtMaxLimit
inspectMaxLimit()
checkMaxLimit()
isAtMaxLimit
```

`isAtMaxLimit` (sem parênteses) lê o cache; `checkMaxLimit()` e `isAtMaxLimit()` fazem nova leitura UIA. O método `inspectMaxLimit()` detalha as evidências. O monitor `busyWindow()` atualiza a cada **8 segundos**, com observação periódica do limite; o laboratório de três mensagens usa estabilidade mínima de **300 ms**. Consulte [docs/chat-max-limit-uia.md](docs/chat-max-limit-uia.md).

# Chat Automation V6.4 — WebInterface + Chat + USE + Runtime Refresh (C# / Windows / Edge UIA3)

Projeto console .NET 8 Windows (compatível com SDKs posteriores, inclusive SDK 11) e FlaUI.UIA3 5.0.0.
Mantém comandos e arquivo `current-binded-chat.json` da V2.

## Instalação

Extrair **`source/`** sobre `C:\alexbonemberger\on-my-own\chat-automation\source`.
O catálogo de conversas fica **fora da pasta do projeto** em `%LOCALAPPDATA%\ChatAutomation\chats.json`.
A configuração `enableSendHumanText` fica em `%LOCALAPPDATA%\ChatAutomation\settings.json` e é carregada ao iniciar o console. O padrão inicial é `false`.

```powershell
Set-Location 'C:\alexbonemberger\on-my-own\chat-automation\source'
& '.\setup.ps1'
& '.\chat-features.ps1' list-chats
& '.\chat-automation.ps1'
```

## Uso em runtime (console interativo)

Na inicialização, `WebInterface` carrega o catálogo persistente e mostra **nome + URL**.
O vínculo anterior da V2 (`current-binded-chat.json`) é migrado automaticamente para `chat00`, caso ainda não esteja catalogado.
O nome é preservado nas próximas execuções. Um nome informado explicitamente reusa ou atualiza esse chat.

Com uma guia Edge selecionada, caso ainda não tenha sido cadastrada:

```powershell
& '.\bind-to-chat.ps1' -Name chat00
```

Dentro da sessão `& '.\chat-automation.ps1'`:

```text
help()
listChats()
web.addChat("chat02", "https://chatgpt.com/c/IDENTIFICADOR")
chat02.getText()                // lê apenas o campo de escrita, nunca o histórico
chat02.clear()                  // limpa apenas o rascunho
web.refresh()                  // relê chats.json quando alterado fora da sessão
chat00.setFocus()
chat00.setText("Teste de rascunho")
chat00.sendMessage()
chat00.sendMessage("Mensagem preparada e enviada em uma chamada")
chat00.open(Mode.NewWindow)
chat00.open(Mode.NewTab)
chat00.detectTextArea()
```

### Selecionar o chat ativo com `use`, como em bancos de dados

O contexto pertence à **sessão do console**. `use` seleciona o chat no catálogo, mas não muda o foco do Edge; `setFocus()` faz isso.

```text
listChats()
use "chat00"
setFocus()
setText("Rascunho sem enviar")
getText()
clear()
setText("Mensagem que será enviada")
sendMessage()
use "chat01"
open(Mode.NewWindow)
use
```

Também aceita `use chat00`, `USE "chat00";` e `web.use("chat00")`.
Após selecionar, todos os métodos `setText`, `getText`, `clear`, `setFocus`, `detectTextArea`, `sendMessage` e `open` aceitam a forma **sem prefixo**. A forma `chat00.setText("texto")` continua funcionando com ou sem `use`.

Sem um chat selecionado, uma chamada como `setText("texto")` **falha explicitamente**, sem escolher um destino arbitrário. Se um chat selecionado for removido ou sua URL mudar externamente, `web.refresh()` limpa a seleção para evitar ações no destino errado.

O prompt indica o **nome completo do projeto selecionado**, mais os identificadores de chat e projeto: `RM0 — MA GPTBOT Base Capabilities[GPTBOT_0001|PRO_0000]>`. Sem projeto ativo, mostra `None[chat00|None]>` ou `None[-|None]>` quando também não há chat selecionado. O nome do projeto vem do cadastro (`fullName`), não do ID nem do executável; `use` (sem nome) consulta o chat ativo.

`setText` **substitui** o rascunho presente no editor e **não o envia**. A mensagem é preparada com o cabeçalho `[dd/MM/yyyy-HH:mm]` na primeira linha (UTC−3 fixo).
`sendMessage()` sem argumento exige `setText()` anterior **na mesma sessão** quando `enableSendHumanText=false`; usa exatamente o timestamp preparado, sem duplicação.
`sendMessage("texto")` prepara o texto marcado e clica no envio em uma chamada; a entrega no serviço remoto não é confirmada. Documentação: [docs/message-timestamp.md](docs/message-timestamp.md).
Não executa expressões C# arbitrárias: o shell aceita apenas esses comandos tipados.

## API pública C#

```csharp
using ChatAutomation.Domain;

using var web = new WebInterface();
var chats = web.ListChats();  // alias web.listChats()
var created = web.AddChat("chat02", "https://chatgpt.com/c/IDENTIFICADOR");
var reloaded = web.Refresh();  // alias web.refresh(); preserva instâncias existentes
var chat00 = web.GetChat("chat00"); // ou web["chat00"]
var current = web.UseChat("chat00"); // ou web.use("chat00")
Console.WriteLine(web.CurrentChatName);
web.RequireCurrentChat().SetFocus(); // chamada sobre o contexto selecionado
chat00.SetFocus();
chat00.SetText("Olá");
var draft = chat00.GetText();
chat00.Clear();
chat00.SetText("Mensagem final");
chat00.SendMessage();
chat00.Open(Mode.NewTab);
chat00.Open(Mode.NewWindow);
```

O nome é um **identificador de catálogo** e não uma variável C# gerada automaticamente.
No shell interativo, a resolução `chat00.setText(...)` é feita pelo catálogo.

## CLI independente (útil ao GitPS7)

```powershell
& '.\chat-features.ps1' list-chats
& '.\chat-features.ps1' help
& '.\chat-features.ps1' add-chat -Name chat02 -Url 'https://chatgpt.com/c/IDENTIFICADOR'
& '.\chat-features.ps1' refresh-chats
& '.\chat-features.ps1' get-text -Chat chat02
& '.\chat-features.ps1' clear-chat -Chat chat02
& '.\bind-to-chat.ps1' -Name chat00
& '.\chat-features.ps1' focus-chat -Chat chat00
& '.\chat-features.ps1' set-text -Chat chat00 -Text 'Somente rascunho'
& '.\chat-features.ps1' send-chat-message -Chat chat00 -Text 'Envio explícito'
& '.\chat-features.ps1' open-chat -Chat chat00 -Mode new-tab
& '.\chat-features.ps1' open-chat -Chat chat00 -Mode new-window
& '.\chat-features.ps1' detect-text-area -Chat chat00
```

Cada execução do CLI é um processo independente; por isso, **`set-text` CLI seguido de `send-message` CLI sem texto não é suportado**.
Use `send-chat-message -Text` ou a sessão shell se quiser `setText` e depois `sendMessage()`.

`bind-to-chat` sem nome registra o chat atual como `chat00`, `chat01`, etc. A mesma URL reaproveita o nome salvo.
Para cadastrar uma URL sem navegar:

```powershell
& '.\bind-to-chat.ps1' -Name chat01 -Url 'https://chatgpt.com/c/IDENTIFICADOR'
```

`list-chats` lista chats **registrados**, não todas as conversas do histórico do site.
O aplicativo não raspa a lista lateral nem tenta descobrir chats remotos não cadastrados.

## Sincronização em runtime

- `web.addChat("nome","url")` grava em `chats.json` e disponibiliza `nome.setText()` na mesma sessão, **sem** precisar de refresh.
- `web.refresh()` relê do disco os chats cadastrados por outros processos (`bind-to-chat.ps1`, `add-chat`), preserva instâncias não removidas e descarta rascunhos pendentes da sessão por segurança. Se a URL do chat selecionado mudar, limpa a seleção.
- `listChats()` lista **somente os chats cadastrados**, não varre o histórico no site.
- `chat00.getText()` lê o **campo de entrada atual** do Edge. Usa UIA `ValuePattern` ou `TextPattern`; se a interface não disponibilizar o texto retorna `READBACK_UNSUPPORTED`. Nunca inventa texto a partir do cache da sessão.
- `chat00.clear()` apaga apenas o texto do editor; não apaga conversas, mensagens antigas nem o cadastro.

## Arquitetura

- `Domain/WebInterface.cs`: instancia os chats cadastrados, fornece `GetChat`, `ListChats`, `AddChat`, `CaptureChat`, `Refresh`, `UseChat`, `CurrentChatName` e `RequireCurrentChat`; `BindToChat` permanece somente para compatibilidade.
- `Domain/WebInterface.cs` / `Chat`: `Name`, `Url`, `GetText`, `Clear`, `SetText`, `SendMessage`, `SetFocus`, `Open`, aliases lowerCamelCase.
- `Domain/ConsoleShell.cs`: sessão persistente para chamadas `chat00.setText(...)` e `use "chat00"` seguido de `setText(...)`.
- `State/ChatRegistry.cs`: registro persistente `%LOCALAPPDATA%\ChatAutomation\chats.json`, com gravação atômica.
- `State/CurrentBinding.cs`: mantém `currentBindedChat` da V2.
- `Features/chat-features.cs`: detecção e interação UIA3.
- `Browser/EdgeBrowser.cs`: reconhecimento do Edge, URL exata, scanner de guias.

## Segurança e limites

1. O nome do chat é apenas um alias para a URL. O HWND pode mudar entre execuções; não é identidade de perfil.
2. `SetFocus` e `SendMessage` requerem correspondência **exata** de URL. Em duplicatas, usam apenas preferência inequívoca de janela/guia selecionada; bloqueiam escolha ambígua e inspeção incompleta.
3. `SetText` só digita após confirmar o foco de teclado do editor por UIA; usa Ctrl+A para substituir o texto. A confirmação de conteúdo tenta `ValuePattern` e `TextPattern` (contenteditable).
4. `SendMessage` só clica após validar um único botão de envio. `INPUT_DISPATCHED_UNCONFIRMED` não comprova entrega ao servidor.
5. `Open(Mode.NewTab)` seleciona uma janela Edge inequivocamente identificada; `Mode.NewWindow` inicia processo Edge. Ambas retornam pedido de abertura, sem garantia de navegação concluída.
6. A leitura de guias pode selecioná-las temporariamente. Evitar rodar durante digitação humana.
7. O código depende da árvore UIA e dos rótulos de acessibilidade do ChatGPT/Edge; mudanças na interface exigem calibração de controles.

## Testes

```powershell
& '.\setup.ps1'                         # restore/build Release + self-test
& '.\chat-features.ps1' list-chats
& '.\chat-features.ps1' help
& '.\chat-features.ps1' inspect-edge
& '.\chat-automation.ps1'
```

**Validação no ambiente de geração:** ZIP/arquivos e verificações estáticas. O executável Windows/UIA3 precisa ser compilado e testado no PC do usuário.


## V6 — Recuperação de foco após fechar o Edge

- `chat00.open(Mode.NewWindow)` e `chat00.open(Mode.NewTab)` só retornam
  `OPENED_VERIFIED` após confirmar a **URL exata** na janela/guia aberta.
- O novo HWND substitui o HWND anterior somente após verificação e passa a ser
  preferido por `setFocus`, `getText`, `setText` e `clear`.
- Um HWND salvo de uma janela fechada não é tratado como identidade durável.
- Janelas extras ainda carregando não bloqueiam um chat já comprovado por URL.
- `setText` reidentifica o editor antes do readback e tenta `ValuePattern`
  apenas quando o teclado não confirmou o texto.
- `sendMessage` exige readback idêntico do campo de composição antes do clique.
- `clear` substitui apenas o rascunho, sem afetar o histórico da conversa.
- Se o Edge estiver inteiramente fechado, `open(Mode.NewTab)` cria uma nova
  janela como fallback e informa `new-window-fallback`.
- `OPEN_UNVERIFIED` e `READBACK_MISMATCH` são estados de bloqueio legítimos:
  não autorizam envio e devem ser diagnosticados via `inspect-edge`.

### Exemplo de teste

```text
use "chat00"
open(Mode.NewWindow)
setFocus()
setText("Teste depois de reabrir")
getText()
clear()
getText()
```

O aplicativo não tem como provar a entrega ao servidor apenas por um clique UIA.

## V6.2 — Auto-foco ao escrever

`setText()` e `clear()` não exigem chamada anterior a `setFocus()`. O aplicativo
valida o vínculo por URL exata, traz a janela ao primeiro plano, tenta o foco
UIA do editor e, se necessário, um clique verificado no editor. Sem confirmação
de foco e de URL, não envia teclas nem mensagens.

```text
use "chat00"
setText("Rascunho sem setFocus")
getText()
clear()
```

Em caso de janela fechada, use `open(Mode.NewWindow)` e continue normalmente.
Se Windows/Edge bloquear o foco ou exibir um diálogo modal, o método falha
com `EDITOR_FOCUS_NOT_VERIFIED` sem digitar.



## V6.3 — Envio de texto digitado manualmente

Dentro de `& '.\chat-automation.ps1'`:

```text
enableSendHumanText                 // mostra valor atual
use "chat00"
enableSendHumanText = true          // grava imediatamente em settings.json
// Digite ou altere uma mensagem no compositor do Edge (fora do console).
getText()                           // lê o compositor
sendMessage()                       // insere timestamp se faltar e tenta enviar
web.setEnableSendHumanText(false)   // pode desativar a qualquer momento
```

Com a opção `false` (padrão), `sendMessage()` exige um `setText()` anterior **na mesma sessão** e bloqueia se o rascunho foi modificado manualmente. Com a opção `true`, lê o texto acessível **diretamente do compositor**, verifica a URL exata e recusa rascunho vazio. Quando faltar timestamp, substitui o rascunho pelo mesmo texto precedido do cabeçalho e valida o readback antes do clique. Mensagens já marcadas não recebem novo prefixo. O retorno `INPUT_DISPATCHED_UNCONFIRMED` significa clique executado, não confirmação de entrega.

Também é possível usar diretamente `chat00.sendMessage()` ou `web.enableSendHumanText = true` em C#.

CLI fora do console:

```powershell
& '.\chat-features.ps1' settings
& '.\chat-features.ps1' set-send-human-text -Enabled true
& '.\chat-features.ps1' set-send-human-text -Enabled false
```

`clear()` agora tenta limpar o editor em até três passagens sob foco UIA confirmado. Se a UIA fornecer uma dica (placeholder) conhecida em lugar de texto vazio, o retorno será `CLEARED_PLACEHOLDER_READBACK` / `KNOWN_PLACEHOLDER`, **não** `DRAFT_VERIFIED` (não há confirmação de campo vazio por valor). Outros resíduos geram erro explícito.


## V6.4 — Leitura do editor vazio

- `getText()` retorna `""` quando o editor está vazio, inclusive quando o UIA expõe `"Pergunte ao ChatGPT\n"`, `"Ask anything"` ou outros placeholders conhecidos.
- Mensagens reais continuam sendo lidas sem alterações; o retorno `READBACK_UNSUPPORTED` permanece para ausência de suporte do provedor UIA.
- `sendMessage()` com `enableSendHumanText = true` continua bloqueando o envio se a leitura corresponder a um editor vazio ou placeholder (`EDITOR_EMPTY`).
- `enableSendHumanText` continua persistido no `settings.json` existente; esta atualização não altera preferências nem cadastro de chats.
- Limitação: se alguém digitar literalmente um texto idêntico a um placeholder conhecido, a leitura UIA isolada pode não distinguir o texto da dica. Para esse caso, use `setText("...")` e `sendMessage("...")` explicitamente.


## Monitor de geração (V6.5)

No console interativo (`& '.\chat-automation.ps1'`):

```text
use "chat00"
setFocus()
isGenerating()                // bool: true durante a geração, conforme controles UIA
isBusy()                      // alias de isGenerating()
busyWindow()                  // novo CMD: chat, estado, contagem 3/2/1, limpa e atualiza
chat00.isBusy()               // sem USE
chat00.busyWindow()           // monitor por nome
```

No PowerShell 7:

```powershell
& '.\chat-features.ps1' is-busy -Chat chat00
& '.\chat-features.ps1' busy-window -Chat chat00
```

`isBusy()` é uma leitura **passiva**: não alterna guia ou janela e não envia comandos. Um botão acessível de interrupção (`Stop generating`, `Parar de gerar` etc.) visível e habilitado indica `true`; a ausência do botão com editor identificado indica `false`. Falhas de leitura, interface incompleta e guia inativa não são interpretadas como `false`: o comando retorna `BLOCKED`; o monitor mostra `UNKNOWN` com o motivo.

### Múltiplas janelas ou guias da mesma conversa

A preferência de janela passou a incluir `PreferredTabRuntimeId` do UIA3 para fixar **uma instância de guia**, além de HWND e URL exata. `open(Mode.NewWindow)` e `open(Mode.NewTab)` atualizam o vínculo com a guia verificada. Se o RuntimeId não existir (ou expirar após reiniciar o Edge), a recuperação usa apenas evidências inequívocas: guia explicitamente selecionada, ocorrência única ou janela legada com uma única ocorrência. A operação bloqueia duplicatas ambíguas (`AMBIGUOUS_CHAT_TABS`).

O monitor não ativa uma guia em segundo plano: selecione a guia e execute `setFocus()` antes de abrir `busyWindow()`. A janela de monitoramento encerra com Ctrl+C; erros aparecem como `UNKNOWN`, jamais como `false` silencioso.
