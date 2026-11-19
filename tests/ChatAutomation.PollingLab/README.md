# ChatAutomation.PollingLab — teste isolado de polling e três envios

O laboratório é um **executável separado**. Não modifica automaticamente o console principal, o catálogo ou a preferência `enableSendHumanText`. Executa somente em Windows com Microsoft Edge e .NET SDK.

## 1. Validação pura (sem Edge e sem envios)

```powershell
& '.\polling-lab.ps1' -Mode self-test
```

Verifica que uma leitura `false` antes de observar `true` **não** autoriza a mensagem seguinte, e que uma oscilação `true` reinicia o período ocioso.

## 2. Medir custo de `isBusy()` sem enviar

Primeiro selecione explicitamente a guia de `chat00` e execute `setFocus()` no console principal. Encerre o console ou deixe-o sem novos comandos. Rode no PowerShell 7:

```powershell
& '.\polling-lab.ps1' -Mode probe -Chat chat00 -IntervalMs 20 -DurationSec 15
```

Retorna: consultas totais, `true`/`false`, erros, taxa efetiva (leituras/s), custo por chamada UIA (mínimo/p50/p95/p99/máximo/média), intervalo real entre chamadas, CPU do processo e CPU aproximada dos processos Edge que permaneceram ativos.

**20 ms não é SLA:** cada leitura enumera controles UIA, confirma URL/guia e pode durar mais de 20 ms. Há um `Sleep(1)` mínimo quando a chamada excede o intervalo para não fazer busy-spin. O consumo atribuído ao Edge é uma aproximação que inclui tarefas não relacionadas. As chamadas UIA são locais e não são chamadas de API de modelo.

## 3. Testar 3 mensagens reais (ação explicitamente armada)

Abra e selecione a guia que deseja controlar; tenha certeza de que é o chat certo. Não deixe rascunhos no editor. Interrompa outros monitores/testes que atuem sobre a mesma conversa.

```powershell
& '.\polling-lab.ps1' -Mode send-three -Chat chat00 -IntervalMs 20 -StableIdleMs 300 -AllowSend
```

São enviados exatamente estes textos, um por vez:

- `Teste automatizado do chat-automation (1/3). Responda somente OK-1.`
- `Teste automatizado do chat-automation (2/3). Responda somente OK-2.`
- `Teste automatizado do chat-automation (3/3). Responda somente OK-3.`

O laboratório:

1. Fixa a guia por `HWND + RuntimeId` (ou bloqueia se a identidade UIA for indisponível).
2. Exige editor vazio e `isBusy()==false` estável por 300 ms antes da primeira mensagem.
3. Envia uma única vez e registra `INPUT_DISPATCHED_UNCONFIRMED`, duração do envio e tempo desde o primeiro `false`.
4. Exige detectar `true` após o envio (até 30 s); depois exige `false` estável por 250 ms (até 180 s) antes da mensagem seguinte.
5. Repete até a terceira resposta terminar. Se houver erro UIA, troca de guia, rascunho inesperado, falta de `true` ou timeout, interrompe **sem repetir** um envio de resultado incerto.

O mutex local evita dois laboratórios simultâneos para a mesma URL, mas **não** impede ações humanas ou de outros processos no Edge.

Os indicadores de `isBusy()` se baseiam no controle acessível de interrupção de geração. A ausência do botão pode ser transitória; 300 ms é uma escolha de teste, não uma confirmação pelo servidor. A leitura `INPUT_DISPATCHED_UNCONFIRMED` só confirma a tentativa de clique. Evite usar esse laboratório para mensagens cujo envio duplicado teria consequências.

## Limites do protocolo atual

- Não há recibo de aceitação do servidor nem ID da mensagem enviada.
- Se uma resposta terminar sem que qualquer sondagem observe `true`, o teste aborta (não infere sucesso).
- 20 ms pode gerar carga significativa de consultas COM/UIA e do provedor Edge; a alternativa de produção é usar eventos UIA com polling de reconciliação.
- Em janelas/guias duplicadas, trocar a guia após iniciar o teste faz o processo falhar fechado.
- Não modifica a `main`; este teste é experimental em branch independente.
