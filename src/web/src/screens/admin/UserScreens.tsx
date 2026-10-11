import { useState } from 'react';
import type { FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { adminApi, masterApi } from '../../api/endpoints';
import type { ApiProblem } from '../../api/problem';
import { asProblem } from '../../api/problem';
import type { AdminUser, Grant, Role, TemporaryPassword, Warehouse, WarehouseLevel } from '../../api/types';
import { ALL_ROLES } from '../../api/types';
import { levelLabel, roleLabel } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { CheckField, ConfirmStep, FormErrorSummary, LiveRegion, SelectField, TextField, fieldDomId, fieldErrorsOf, useDisclosure } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { Icon } from '../../components/Icon';
import { EmptyState, InfoNote, ProblemAlert, QueryView, StaleAlert, SuccessNotice } from '../../components/states';
import { Button, Code, Meta, PageHead, Tag } from '../../components/ui';
import { formatDecimal, formatMoney, parseMoney } from '../../lib/format';
import { useApiQuery, useInvalidate, useSetCached } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';
import { KLink } from '../../shell/context';

const USER_NAME = /^[a-z0-9._-]{3,64}$/;

/** A temporary password is returned once by the server and shown once here; it is never stored. */
function TemporaryPasswordNotice({ result, context, onDismiss }: { result: TemporaryPassword; context: 'created' | 'reset'; onDismiss: () => void }) {
  const [copied, setCopied] = useState<'no' | 'yes' | 'failed'>('no');
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(result.temporaryPassword);
      setCopied('yes');
    } catch {
      setCopied('failed');
    }
  };

  return (
    <div className="alert alert--warn" role="alert">
      <Icon name="warn" />
      <h2>
        {context === 'created' ? 'Kullanıcı oluşturuldu' : 'Parola sıfırlandı'}: geçici parola yalnız şimdi gösteriliyor
      </h2>
      <p>
        <strong>{result.user.displayName}</strong> (<Code>{result.user.userName}</Code>) için geçici parola aşağıdadır. Sunucu bu parolayı bir daha
        göstermez; bu kutuyu kapattığınızda ya da sayfadan ayrıldığınızda tekrar görüntülenemez. Kullanıcıya güvenli bir yoldan iletin; ilk girişte
        değiştirmesi zorunludur.
      </p>
      <p className="secret">
        <span className="secret__value" translate="no" data-testid="temporary-password">
          {result.temporaryPassword}
        </span>
        <Button onClick={() => void copy()}>Parolayı panoya kopyala</Button>
      </p>
      <p aria-live="polite">
        {copied === 'yes' && 'Parola panoya kopyalandı. İlettikten sonra panoyu başka bir şey kopyalayarak temizleyin.'}
        {copied === 'failed' && 'Panoya kopyalanamadı; parolayı seçip elle kopyalayın.'}
      </p>
      <div className="actions">
        <Button onClick={onDismiss}>Parolayı ilettim, ekrandan kaldır</Button>
      </div>
    </div>
  );
}

function RoleChecks({ formId, roles, onChange, error }: { formId: string; roles: Role[]; onChange: (roles: Role[]) => void; error?: string | undefined }) {
  return (
    <fieldset aria-describedby={error !== undefined ? `${formId}-roles-hata` : undefined}>
      <legend className="legend">Roller</legend>
      <div className="checks">
        {ALL_ROLES.map((role) => (
          <CheckField
            key={role}
            formId={formId}
            name={`role-${role}`}
            label={roleLabel(role)}
            checked={roles.includes(role)}
            onChange={(checked) => onChange(checked ? [...roles, role] : roles.filter((r) => r !== role))}
          />
        ))}
      </div>
      {error !== undefined && (
        <span className="field-error" id={`${formId}-roles-hata`}>
          <Icon name="error" />
          {error}
        </span>
      )}
      <p className="small">
        Rol tek başına depo işlemi yetkisi vermez; depo yetkisi ayrıca atanır. Sistem yöneticisi rolü ticari belgeleri ve stoğu okuma yetkisi içermez.
      </p>
    </fieldset>
  );
}

// ---------------------------------------------------------------- list + create
const CREATE_FORM = 'kullanici-yeni';

function CreateUserForm({ onCreated, onClose }: { onCreated: (result: TemporaryPassword) => void | Promise<void>; onClose: () => void }) {
  const [userName, setUserName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [roles, setRoles] = useState<Role[]>([]);
  const [limit, setLimit] = useState('0');
  const [local, setLocal] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(0);

  const create = useCommand((input: { userName: string; displayName: string; roles: Role[]; poApprovalLimit: number }) => adminApi.createUser(input), {
    onSuccess: onCreated,
    onProblem: () => setAttempt((n) => n + 1),
  });

  const server = fieldErrorsOf(create.problem);
  if (create.problem?.code === 'user_name_taken') {
    server.userName = create.problem.detail ?? 'Bu kullanıcı adı kullanılıyor.';
  }

  const errorOf = (key: string): string | undefined => local[key] ?? server[key];
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const name = userName.trim().toLocaleLowerCase('en-US');
    if (!USER_NAME.test(name)) {
      next.userName = '3–64 karakter; yalnız a–z, 0–9 ve . - _ kullanılabilir.';
    }

    if (displayName.trim().length < 2 || displayName.trim().length > 100) {
      next.displayName = 'Ad 2–100 karakter olmalıdır.';
    }

    const amount = parseMoney(limit);
    if (amount === null) {
      next.poApprovalLimit = 'Tutar 0 veya daha büyük, en fazla 2 ondalıklı olmalıdır.';
    }

    setLocal(next);
    create.clearProblem();
    if (Object.keys(next).length > 0 || amount === null) {
      setAttempt((n) => n + 1);
      return;
    }

    void create.run({ userName: name, displayName: displayName.trim(), roles, poApprovalLimit: amount });
  };

  const labels: Record<string, string> = { userName: 'Kullanıcı adı', displayName: 'Ad soyad', roles: 'Roller', poApprovalLimit: 'Satın alma onay limiti' };
  const summary: SummaryItem[] = [];
  for (const key of Object.keys(labels)) {
    const message = errorOf(key);
    if (message !== undefined) {
      summary.push({ fieldId: key === 'roles' ? fieldDomId(CREATE_FORM, `role-${ALL_ROLES[0] ?? ''}`) : fieldDomId(CREATE_FORM, key), message: `${labels[key] ?? key}: ${message}` });
    }
  }

  const problem = create.problem;
  return (
    <form className="region region--pad stack" id="kullanici-yeni-alani" onSubmit={submit} noValidate aria-labelledby="kullanici-yeni-baslik">
      <h2 className="section-h section-h--flush" id="kullanici-yeni-baslik">
        Yeni kullanıcı
      </h2>
      {summary.length > 0 && (
        <FormErrorSummary title={`Kullanıcı oluşturulmadı: ${summary.length} alanı düzeltin`} items={summary} focusKey={attempt} note="Kullanıcı kaydedilmedi." />
      )}
      {problem !== null && summary.length === 0 && (
        <ProblemAlert problem={problem} title="Kullanıcı oluşturulamadı">
          {problem.isNetwork && (
            <p>
              Bu işlem geçici parola döndürdüğü için otomatik yeniden denenmez. Kullanıcı oluşturulmuş olabilir: listeyi yenileyin; kullanıcı listede
              varsa sayfasından parolasını sıfırlayın, yoksa formu yeniden gönderin.
            </p>
          )}
        </ProblemAlert>
      )}
      <div className="formgrid">
        <TextField
          formId={CREATE_FORM}
          name="userName"
          label="Kullanıcı adı"
          value={userName}
          onChange={setUserName}
          inputClassName="code"
          spellCheck={false}
          autoCapitalize="none"
          maxLength={64}
          required
          translate="no"
          error={errorOf('userName')}
          hint="Girişte kullanılır; sonradan değiştirilemez."
        />
        <TextField formId={CREATE_FORM} name="displayName" label="Ad soyad" value={displayName} onChange={setDisplayName} maxLength={100} required error={errorOf('displayName')} />
        <TextField
          formId={CREATE_FORM}
          name="poApprovalLimit"
          label="Satın alma onay limiti (TRY)"
          value={limit}
          onChange={setLimit}
          inputClassName="input--money"
          inputMode="decimal"
          error={errorOf('poApprovalLimit')}
          hint="Yalnız satın alma uzmanı için anlamlıdır. 0 ise hiçbir satın alma siparişini onaylayamaz."
        />
      </div>
      <RoleChecks formId={CREATE_FORM} roles={roles} onChange={setRoles} error={errorOf('roles')} />
      <div className="actions actions--end">
        <Button onClick={onClose}>Vazgeç</Button>
        <Button type="submit" variant="primary" pending={create.pending} pendingLabel="Kullanıcı oluşturuluyor…">
          Kullanıcıyı oluştur
        </Button>
      </div>
    </form>
  );
}

export function UsersScreen() {
  const me = useMe();
  const invalidate = useInvalidate();
  const query = useApiQuery(['admin-users'], (signal) => adminApi.users(signal));
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<TemporaryPassword | null>(null);

  const columns: Column<AdminUser>[] = [
    {
      id: 'userName',
      header: 'Kullanıcı adı',
      kind: 'code',
      rowHeader: true,
      cell: (u) => (
        <KLink to={`/yonetim/kullanicilar/${u.id}`} translate="no">
          {u.userName}
        </KLink>
      ),
    },
    { id: 'name', header: 'Ad soyad', kind: 'name', cell: (u) => (u.id === me.id ? `${u.displayName} (siz)` : u.displayName) },
    { id: 'roles', header: 'Roller', kind: 'name', cell: (u) => (u.roles.length > 0 ? u.roles.map(roleLabel).join(' · ') : <span className="muted">Rol yok</span>) },
    { id: 'grants', header: 'Depo yetkisi', kind: 'num', cell: (u) => u.grants.length },
    { id: 'limit', header: 'Onay limiti', kind: 'num', cell: (u) => formatMoney(u.poApprovalLimit) },
    {
      id: 'state',
      header: 'Durum',
      cell: (u) =>
        !u.isActive ? (
          <Tag glyph="cancel">Pasif</Tag>
        ) : u.mustChangePassword ? (
          <Tag tone="warn" glyph="half">
            Aktif · geçici parolada
          </Tag>
        ) : (
          <Tag tone="ok" glyph="done">
            Aktif
          </Tag>
        ),
    },
  ];

  return (
    <div className="view">
      <PageHead
        title="Kullanıcılar"
        lead="Hesaplar, roller, depo yetkileri ve satın alma onay limitleri. Açık kayıt yoktur; her hesabı sistem yöneticisi açar."
        actions={
          <Button variant={creating ? 'default' : 'primary'} aria-expanded={creating} aria-controls="kullanici-yeni-alani" onClick={() => setCreating((value) => !value)}>
            {creating ? 'Yeni kullanıcı formunu kapat' : 'Yeni kullanıcı'}
          </Button>
        }
      />
      {created !== null && <TemporaryPasswordNotice result={created} context="created" onDismiss={() => setCreated(null)} />}
      {creating && (
        <CreateUserForm
          onClose={() => setCreating(false)}
          onCreated={async (result) => {
            setCreated(result);
            setCreating(false);
            await invalidate('admin-users');
          }}
        />
      )}
      <QueryView query={query} loadingLabel="Kullanıcılar yükleniyor…">
        {(users) =>
          users.length === 0 ? (
            <EmptyState title="Kullanıcı yok">Yeni kullanıcı düğmesiyle ilk hesabı açın.</EmptyState>
          ) : (
            <section className="region tablewrap">
              <DataTable caption="Kullanıcılar" captionNote={`${users.length} hesap`} columns={columns} rows={users} rowKey={(u) => u.id} />
            </section>
          )
        }
      </QueryView>
    </div>
  );
}

// ---------------------------------------------------------------- user detail
const ROLES_FORM = 'kullanici-roller';
const GRANTS_FORM = 'kullanici-depolar';
const LEVELS: readonly WarehouseLevel[] = ['View', 'Operate', 'Supervise'];

function RolesForm({ user, onSaved, onStale }: { user: AdminUser; onSaved: (user: AdminUser, message: string) => void | Promise<void>; onStale: () => void }) {
  // Not re-initialised when the user is reloaded after a 412, so the admin's choices survive.
  const [roles, setRoles] = useState<Role[]>(user.roles);
  const [limit, setLimit] = useState(formatDecimal(user.poApprovalLimit));
  const [limitError, setLimitError] = useState<string | undefined>(undefined);
  const save = useCommand((args: { roles: Role[]; limit: number; etag: string }) => adminApi.setRoles(user.id, args.roles, args.limit, args.etag), {
    onSuccess: (result) => onSaved(result, 'Roller ve onay limiti kaydedildi. Kullanıcının açık oturumları kapatıldı; yeniden giriş yapması gerekir.'),
  });

  const server = fieldErrorsOf(save.problem);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const amount = parseMoney(limit);
    if (amount === null) {
      setLimitError('Tutar 0 veya daha büyük, en fazla 2 ondalıklı olmalıdır.');
      document.getElementById(fieldDomId(ROLES_FORM, 'poApprovalLimit'))?.focus();
      return;
    }

    setLimitError(undefined);
    void save.run({ roles, limit: amount, etag: user.eTag });
  };

  const problem = save.problem;
  return (
    <form className="region region--pad stack" onSubmit={submit} noValidate aria-labelledby="roller-baslik">
      <h2 className="section-h section-h--flush" id="roller-baslik">
        Roller ve onay limiti
      </h2>
      {problem !== null && problem.isStale && <StaleAlert what="kullanıcı" onReload={onStale} problem={problem} />}
      {problem !== null && !problem.isStale && <ProblemAlert problem={problem} title="Roller kaydedilemedi" hideFieldErrors />}
      <RoleChecks formId={ROLES_FORM} roles={roles} onChange={setRoles} error={server.roles} />
      <TextField
        formId={ROLES_FORM}
        name="poApprovalLimit"
        label="Satın alma onay limiti (TRY)"
        value={limit}
        onChange={setLimit}
        inputClassName="input--money"
        fieldClassName="measure-form"
        inputMode="decimal"
        error={limitError ?? server.poApprovalLimit}
        hint="Toplamı bu tutarı aşan satın alma siparişini onaylayamaz. 0 ise hiçbirini onaylayamaz."
      />
      <div className="actions actions--end">
        <Button type="submit" pending={save.pending} pendingLabel="Kaydediliyor…">
          Rolleri ve limiti kaydet
        </Button>
      </div>
    </form>
  );
}

function GrantsForm({
  user,
  warehouses,
  onSaved,
  onStale,
}: {
  user: AdminUser;
  warehouses: Warehouse[];
  onSaved: (user: AdminUser, message: string) => void | Promise<void>;
  onStale: () => void;
}) {
  const [levels, setLevels] = useState<Record<string, string>>(() => Object.fromEntries(user.grants.map((g) => [g.warehouseId, g.level])));
  const save = useCommand((args: { grants: Grant[]; etag: string }) => adminApi.setGrants(user.id, args.grants, args.etag), {
    onSuccess: (result) => onSaved(result, 'Depo yetkileri kaydedildi. Değişiklik kullanıcının bir sonraki isteğinde geçerlidir.'),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const grants: Grant[] = [];
    for (const [warehouseId, value] of Object.entries(levels)) {
      const level = LEVELS.find((l) => l === value);
      if (level !== undefined) {
        grants.push({ warehouseId, level });
      }
    }

    void save.run({ grants, etag: user.eTag });
  };

  // Grants for warehouses that the list does not contain are kept as they are.
  const unknown = user.grants.filter((g) => !warehouses.some((w) => w.id === g.warehouseId));
  const problem = save.problem;
  return (
    <form className="region region--pad stack" onSubmit={submit} noValidate aria-labelledby="depolar-baslik">
      <h2 className="section-h section-h--flush" id="depolar-baslik">
        Depo yetkileri
      </h2>
      <p className="small">
        Görüntüleme: stok ve o depoya bağlı belgeleri görür. İşlem: sevkiyat, mal kabul, transfer. Sorumlu: stok düzeltmesi dahil. Kaydettiğinizde
        buradaki liste kullanıcının tüm depo yetkilerinin yerine geçer.
      </p>
      {problem !== null && problem.isStale && <StaleAlert what="kullanıcı" onReload={onStale} problem={problem} />}
      {problem !== null && !problem.isStale && <ProblemAlert problem={problem} title="Depo yetkileri kaydedilemedi" />}
      {warehouses.length === 0 ? (
        <p className="small">Tanımlı depo yok. Önce Ana veri ekranından depo ekleyin.</p>
      ) : (
        <div className="formgrid">
          {warehouses.map((warehouse) => (
            <SelectField
              key={warehouse.id}
              formId={GRANTS_FORM}
              name={`grant-${warehouse.code}`}
              label={`${warehouse.code} · ${warehouse.name}${warehouse.isActive ? '' : ' (pasif)'}`}
              value={levels[warehouse.id] ?? ''}
              onChange={(value) => setLevels((previous) => ({ ...previous, [warehouse.id]: value }))}
              placeholder="Yetki yok"
              options={LEVELS.map((level) => ({ value: level, label: levelLabel(level) }))}
            />
          ))}
        </div>
      )}
      {unknown.length > 0 && <p className="small">{unknown.length} depo yetkisi listede olmayan bir depoya ait; kaydettiğinizde korunur.</p>}
      <div className="actions actions--end">
        <Button type="submit" pending={save.pending} pendingLabel="Kaydediliyor…">
          Depo yetkilerini kaydet
        </Button>
      </div>
    </form>
  );
}

function UserDocument({ user, onReload }: { user: AdminUser; onReload: () => void }) {
  const me = useMe();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const warehouses = useApiQuery(['warehouses'], (signal) => masterApi.warehouses(signal));
  const resetStep = useDisclosure('kullanici-sifirla');
  const deactivateStep = useDisclosure('kullanici-pasif');
  const [notice, setNotice] = useState<string | null>(null);
  const [temporary, setTemporary] = useState<TemporaryPassword | null>(null);
  const self = user.id === me.id;

  const applied = async (result: AdminUser, message: string) => {
    setCached(['admin-user', user.id], result);
    setNotice(message);
    await invalidate('admin-users');
  };

  const reset = useCommand(() => adminApi.resetPassword(user.id), {
    onSuccess: async (result) => {
      resetStep.hide();
      setTemporary(result);
      setNotice(null);
      setCached(['admin-user', user.id], result.user);
      await invalidate('admin-users');
    },
  });
  const setActive = useCommand((active: boolean) => adminApi.setActive(user.id, active), {
    onSuccess: (result, active) => {
      deactivateStep.hide();
      return applied(result, active ? 'Hesap etkinleştirildi.' : 'Hesap pasifleştirildi; açık oturumları kapatıldı ve giriş yapamaz.');
    },
  });

  const actionProblem: ApiProblem | null = reset.problem ?? setActive.problem;
  return (
    <>
      <PageHead
        title={user.displayName}
        sub={
          <>
            <Code>{user.userName}</Code>
            {user.isActive ? (
              <Tag tone="ok" glyph="done">
                Aktif
              </Tag>
            ) : (
              <Tag glyph="cancel">Pasif</Tag>
            )}
            {user.mustChangePassword && (
              <Tag tone="warn" glyph="half">
                Geçici parolada: ilk girişte değiştirmesi zorunlu
              </Tag>
            )}
            {self && <Tag tone="plain">Bu sizin hesabınız</Tag>}
          </>
        }
        meta={
          <Meta
            items={[
              { label: 'Roller', value: user.roles.length > 0 ? user.roles.map(roleLabel).join(' · ') : 'Rol yok' },
              { label: 'Depo yetkisi sayısı', value: <span className="num">{user.grants.length}</span> },
              { label: 'Satın alma onay limiti', value: <span className="num">{formatMoney(user.poApprovalLimit)}</span> },
            ]}
          />
        }
        actions={
          <KLink className="btn" to="/yonetim/kullanicilar">
            Kullanıcılara dön
          </KLink>
        }
      />

      <LiveRegion>{notice !== null && <SuccessNotice title={notice} />}</LiveRegion>
      {temporary !== null && <TemporaryPasswordNotice result={temporary} context="reset" onDismiss={() => setTemporary(null)} />}
      {actionProblem !== null && (
        <ProblemAlert problem={actionProblem} title={reset.problem !== null ? 'Parola sıfırlanamadı' : 'Hesap durumu değiştirilemedi'}>
          {reset.problem !== null && reset.problem.isNetwork && (
            <p>Yanıt alınamadı: parola sıfırlanmış olabilir ama geçici parola size ulaşmadı. Sıfırlamayı yeniden yapın; önceki geçici parola geçersiz olur.</p>
          )}
        </ProblemAlert>
      )}

      <RolesForm user={user} onSaved={applied} onStale={onReload} />

      {warehouses.isPending ? (
        <p role="status" aria-busy="true" className="small">
          Depolar yükleniyor…
        </p>
      ) : warehouses.isError ? (
        <ProblemAlert problem={asProblem(warehouses.error)} title="Depo listesi yüklenemedi" onRetry={() => void warehouses.refetch()} />
      ) : (
        <GrantsForm user={user} warehouses={warehouses.data} onSaved={applied} onStale={onReload} />
      )}

      <section className="region region--pad stack" aria-labelledby="hesap-baslik">
        <h2 className="section-h section-h--flush" id="hesap-baslik">
          Hesap işlemleri
        </h2>
        <ConfirmStep
          disclosure={resetStep}
          confirmLabel="Parolayı sıfırla"
          pendingLabel="Sıfırlanıyor…"
          pending={reset.pending}
          onConfirm={() => void reset.run(null)}
          question={
            <>
              <strong>
                <Code>{user.userName}</Code> hesabının parolası sıfırlansın mı?
              </strong>{' '}
              Mevcut parola geçersiz olur, tüm oturumları kapatılır ve size bir kez gösterilecek geçici bir parola üretilir.
            </>
          }
        />
        <ConfirmStep
          disclosure={deactivateStep}
          confirmLabel="Hesabı pasifleştir"
          pendingLabel="Pasifleştiriliyor…"
          pending={setActive.pending}
          onConfirm={() => void setActive.run(false)}
          question={
            <>
              <strong>
                <Code>{user.userName}</Code> hesabı pasifleştirilsin mi?
              </strong>{' '}
              Kullanıcı giriş yapamaz ve açık oturumları kapatılır. Belgeleri ve kayıtları korunur; hesap sonradan yeniden etkinleştirilebilir.
            </>
          }
        />
        <div className="actions">
          <Button aria-expanded={resetStep.open} aria-controls={resetStep.panelId} onClick={resetStep.toggle}>
            Parolayı sıfırla…
          </Button>
          <span className="spacer" />
          {user.isActive ? (
            <Button
              variant="danger"
              aria-expanded={deactivateStep.open}
              aria-controls={deactivateStep.panelId}
              onClick={deactivateStep.toggle}
            >
              Hesabı pasifleştir…
            </Button>
          ) : (
            <Button onClick={() => void setActive.run(true)} pending={setActive.pending} pendingLabel="Etkinleştiriliyor…">
              Hesabı etkinleştir
            </Button>
          )}
        </div>
        {self && (
          <InfoNote title="Kendi hesabınız">
            <p>Kendi sistem yöneticisi rolünüzü kaldıramaz ve kendi hesabınızı pasifleştiremezsiniz; sunucu bu istekleri reddeder.</p>
          </InfoNote>
        )}
      </section>
    </>
  );
}

export function UserDetailScreen() {
  const { id = '' } = useParams();
  const query = useApiQuery(['admin-user', id], (signal) => adminApi.user(id, signal));
  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Kullanıcı yükleniyor…"
        what="Kullanıcı"
        notFoundAction={
          <KLink className="btn" to="/yonetim/kullanicilar">
            Kullanıcılara dön
          </KLink>
        }
      >
        {(user) => <UserDocument user={user} onReload={() => void query.refetch()} />}
      </QueryView>
    </div>
  );
}
