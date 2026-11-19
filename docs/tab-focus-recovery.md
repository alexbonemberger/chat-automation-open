# Recuperação de foco — guia vinculada

## Invariante

Para operações que alteram o editor (`clear()`, `setText()`, `sendMessage()`), a guia alvo deve coincidir com o vínculo persistido (`HWND + RuntimeId + URL exata`) **antes** de qualquer tecla ou clique.

- Se a guia original continuar aberta mas estiver em segundo plano, `FocusExact()` recupera a guia pelo **RuntimeId**, ativa a janela e comprova a URL.
- Se o usuário trocar para outra aba durante `clear()`, a recuperação reativa a guia original pelo mesmo RuntimeId e verifica novamente o vínculo.
- Se a guia original desapareceu da janela conhecida, o comando bloqueia com `PINNED_TAB_NOT_FOUND`; não escolhe uma cópia com a mesma URL.
- Se a janela original foi fechada, permanece o mecanismo existente de recuperação por URL, restrito a correspondência inequívoca.
- Se o provedor UIA não disponibilizar RuntimeId, uma guia inativa não pode ser reativada por índice de maneira confiável: falha fechada.
- `isBusy()` e `inspectMaxLimit()` continuam **passivos**: não alternam guias para executar uma sondagem.
- Troca contínua de foco simultânea à escrita pode impedir a verificação final. O bloqueio é preferível a enviar teclas para o chat errado.

## Teste de regressão (Windows)

1. Abra `chat00` no Edge e registre um rascunho descartável com `setText("rascunho para limpar")`.
2. **Sem fechar a janela**, selecione a guia `GPTBOT_0001` no Edge.
3. Volte ao console interativo e execute `use "chat00"`, `clear()`, `getText()` **sem executar `setFocus()`**.
4. Esperado: `clear()` ativa a guia `chat00`, retorna `DRAFT_VERIFIED` ou `CLEARED_PLACEHOLDER_READBACK`, e `getText()` retorna `""`.
5. Registre outra mensagem descartável em `chat00` e crie uma **segunda guia da mesma URL**. Depois volte para a guia `GPTBOT_0001` e execute `chat00.clear()`.
6. Esperado: somente a guia original do `chat00` é modificada; não selecionar a cópia da mesma URL.
7. Se a guia original for fechada mas a janela continuar aberta, `clear()` deve bloquear com `PINNED_TAB_NOT_FOUND`. Para migrar o vínculo, selecione explicitamente a nova guia e use `bindToChat("chat00")` após revisar a URL.

A compilação `setup.ps1` inclui testes puros `PinnedTabIndex()` para seleção exata, ausência de identidade, identidade ambígua e RuntimeId duplicado, além da validação pré-existente de guias duplicadas. Os testes reais de reativação ainda devem ser executados no Edge.

## Tratamento de um limite terminal

O estado `GPTBOT_0001.isAtMaxLimit` é independente do foco. `clear()` pode limpar um rascunho local quando a guia for recuperada, mas `sendMessage()` permanece bloqueado com `CHAT_AT_MAX_LIMIT`.
