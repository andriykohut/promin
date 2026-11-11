[CustomMessages]
; Included by agent.iss and console.iss. Promin's programs are not signed (#49), so Smart App Control blocks them. In
; Evaluation it blocks nothing yet, but Windows can switch it to On by itself, after which Promin stops starting. Either
; state shows a page that keeps setup from going on until Smart App Control is off.
english.SacPageCaption=Before installing
english.SacPageDescription=Smart App Control has to be off on this PC.
english.SacHeading=IMPORTANT: Windows will block Promin on this PC
english.SacBody=Smart App Control is set to On or Evaluation here, and Promin's programs are not signed yet. Windows blocks them, now or later on its own. Turn Smart App Control off before installing:%n%n1. Open Start, type Windows Security and open it.%n2. Choose App & browser control.%n3. Under Smart App Control, choose Smart App Control settings.%n4. Choose Off and confirm.%n5. Come back here and choose Check again.%n%nIt has to stay off for Promin to keep working. If someone else manages this PC, ask them first.
english.SacLink=Full instructions, and what to do if the switch is greyed out
english.SacUrl=https://github.com/andriykohut/promin/blob/main/docs/smart-app-control-en.md
english.SacCheckAgain=Check again
english.SacStillOn=Smart App Control is still not off. Set it to Off in Windows Security, then choose Check again.
ukrainian.SacPageCaption=Перед встановленням
ukrainian.SacPageDescription=На цьому ПК треба вимкнути інтелектуальне керування програмами.
ukrainian.SacHeading=ВАЖЛИВО: Windows заблокує Promin на цьому ПК
ukrainian.SacBody=Тут інтелектуальне керування програмами (Smart App Control) увімкнуто або працює в режимі оцінювання, а програми Promin поки не мають цифрового підпису. Windows блокує їх — одразу або згодом сама. Перед встановленням вимкніть інтелектуальне керування програмами:%n%n1. Відкрийте «Пуск», введіть «Безпека у Windows» і відкрийте цю програму.%n2. Виберіть «Керування програмами та браузером».%n3. У розділі «Інтелектуальне керування програмами» виберіть «Параметри інтелектуального керування програмами».%n4. Виберіть «Вимкнуто» й підтвердьте.%n5. Поверніться сюди й натисніть «Перевірити ще раз».%n%nЩоб Promin і надалі працював, воно має залишатися вимкненим. Якщо цим ПК керує хтось інший, спершу отримайте дозвіл.
ukrainian.SacLink=Докладна інструкція й що робити, якщо перемикач неактивний
ukrainian.SacUrl=https://github.com/andriykohut/promin/blob/main/docs/smart-app-control-uk.md
ukrainian.SacCheckAgain=Перевірити ще раз
ukrainian.SacStillOn=Інтелектуальне керування програмами досі не вимкнуто. Виберіть «Вимкнуто» в програмі «Безпека у Windows», а потім натисніть «Перевірити ще раз».

[Code]
var
  SacPage: TWizardPage;
  SacCheckAgainButton: TNewButton;

// True when Smart App Control is On or in Evaluation; false when it is Off or this Windows has no Smart App Control.
function SmartAppControlBlocks(): Boolean;
var
  State: Cardinal;
begin
  // 0 = Off, 1 = On, 2 = Evaluation.
  Result := RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Control\CI\Policy', 'VerifiedAndReputablePolicyState', State)
    and (State <> 0);
end;

procedure SacLinkClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  // Setup is elevated; the browser should not be.
  ShellExecAsOriginalUser('open', CustomMessage('SacUrl'), '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure SacCheckAgainClick(Sender: TObject);
begin
  if SmartAppControlBlocks() then
    MsgBox(CustomMessage('SacStillOn'), mbError, MB_OK)
  else
    WizardForm.NextButton.Enabled := True;
end;

procedure InitializeWizard();
var
  Heading, Body, Link: TNewStaticText;
begin
  SacPage := CreateCustomPage(wpWelcome, CustomMessage('SacPageCaption'), CustomMessage('SacPageDescription'));

  Heading := TNewStaticText.Create(SacPage);
  Heading.Parent := SacPage.Surface;
  Heading.Width := SacPage.SurfaceWidth;
  Heading.WordWrap := True;
  Heading.Font.Style := [fsBold];
  Heading.Font.Color := clRed;
  Heading.Caption := CustomMessage('SacHeading');

  Body := TNewStaticText.Create(SacPage);
  Body.Parent := SacPage.Surface;
  Body.Top := Heading.Top + Heading.Height + ScaleY(12);
  Body.Width := SacPage.SurfaceWidth;
  Body.WordWrap := True;
  // "App & browser control" would otherwise underline the "b" as a shortcut key.
  Body.ShowAccelChar := False;
  Body.Caption := CustomMessage('SacBody');

  Link := TNewStaticText.Create(SacPage);
  Link.Parent := SacPage.Surface;
  Link.Top := Body.Top + Body.Height + ScaleY(12);
  Link.Width := SacPage.SurfaceWidth;
  Link.WordWrap := True;
  Link.Font.Style := [fsUnderline];
  Link.Font.Color := clHotLight;
  Link.Cursor := crHand;
  Link.OnClick := @SacLinkClick;
  Link.Caption := CustomMessage('SacLink');

  // Sits where Back would be; Back is hidden on this, the first page.
  SacCheckAgainButton := TNewButton.Create(WizardForm);
  SacCheckAgainButton.Parent := WizardForm;
  SacCheckAgainButton.Caption := CustomMessage('SacCheckAgain');
  SacCheckAgainButton.Width := WizardForm.CalculateButtonWidth([CustomMessage('SacCheckAgain')]);
  SacCheckAgainButton.Height := WizardForm.NextButton.Height;
  SacCheckAgainButton.Top := WizardForm.NextButton.Top;
  SacCheckAgainButton.Left := WizardForm.NextButton.Left - SacCheckAgainButton.Width - ScaleX(10);
  SacCheckAgainButton.Anchors := [akRight, akBottom];
  SacCheckAgainButton.OnClick := @SacCheckAgainClick;
  SacCheckAgainButton.Visible := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SacPage.ID) and not SmartAppControlBlocks();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  SacCheckAgainButton.Visible := CurPageID = SacPage.ID;
  if CurPageID = SacPage.ID then
    WizardForm.NextButton.Enabled := False;
end;
