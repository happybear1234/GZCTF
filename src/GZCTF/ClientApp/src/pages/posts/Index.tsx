import { Button, Pagination, Stack } from '@mantine/core'
import { mdiPlus } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import { FC, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { PostCard } from '@Components/PostCard'
import { WithNavBar } from '@Components/WithNavbar'
import { RequireRole } from '@Components/WithRole'
import { showErrorNotification } from '@Utils/ApiHelper'
import { OnceSWRConfig } from '@Hooks/useConfig'
import { usePageTitle } from '@Hooks/usePageTitle'
import { useUserRole } from '@Hooks/useUser'
import api, { PostInfoModel, Role } from '@Api'
import btnClasses from '@Styles/FixedButton.module.css'
import misc from '@Styles/Misc.module.css'

const ITEMS_PER_PAGE = 10

const Posts: FC = () => {
  const { data: posts, mutate } = api.info.useInfoGetPosts(OnceSWRConfig)

  const [activePage, setPage] = useState(1)
  const { role } = useUserRole()

  const { t } = useTranslation()

  usePageTitle(t('post.title.index'))

  const onTogglePinned = async (post: PostInfoModel, setDisabled: (value: boolean) => void) => {
    setDisabled(true)

    try {
      const res = await api.edit.editUpdatePost(post.id, {
        title: post.title,
        isPinned: !post.isPinned,
      })
      if (post.isPinned) {
        mutate([
          ...(posts?.filter((p) => p.id !== post.id && p.isPinned) ?? []),
          { ...res.data },
          ...(posts?.filter((p) => p.id !== post.id && !p.isPinned) ?? []),
        ])
      } else {
        mutate([
          { ...res.data },
          ...(posts?.filter((p) => p.id !== post.id && p.isPinned) ?? []),
          ...(posts?.filter((p) => p.id !== post.id && !p.isPinned) ?? []),
        ])
      }
      api.info.mutateInfoGetLatestPosts()
    } catch (e) {
      showErrorNotification(e, t)
    } finally {
      setDisabled(false)
    }
  }

  return (
    <WithNavBar isLoading={!posts} minWidth={0} withHeader stickyHeader>
      <Stack justify="space-between" mb="3rem">
        {posts
          ?.slice((activePage - 1) * ITEMS_PER_PAGE, activePage * ITEMS_PER_PAGE)
          .map((post) => <PostCard key={post.id} post={post} onTogglePinned={onTogglePinned} />)}
        {(posts?.length ?? 0) > ITEMS_PER_PAGE && (
          <Pagination
            my={20}
            value={activePage}
            onChange={setPage}
            total={Math.ceil((posts?.length ?? 0) / ITEMS_PER_PAGE)}
            classNames={{
              root: cx(misc.flex, misc.justifyCenter, misc.flexRow),
            }}
          />
        )}
      </Stack>
      {RequireRole(Role.Admin, role) && (
        <Button
          component={Link}
          className={btnClasses.root}
          __vars={{
            '--fixed-right': 'calc(0.1 * (100vw - 70px - 2rem) + 1rem)',
          }}
          variant="filled"
          radius="xl"
          size="md"
          leftSection={<Icon path={mdiPlus} size={1} />}
          to="/posts/new/edit"
        >
          {t('post.button.new')}
        </Button>
      )}
    </WithNavBar>
  )
}

export default Posts
